// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SharpEmu.SourceGenerators;

/// <summary>
/// Build-time enforcement for [SysAbiExport] declarations: duplicate NIDs, malformed
/// NIDs, NIDs that contradict their export name (checked with the PS NID computation),
/// handler signatures the dispatcher cannot call, and — when scripts/ps5_names.txt is
/// wired up as an AdditionalFile — export names unknown to the symbol catalog.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SysAbiExportAnalyzer : DiagnosticAnalyzer
{
    private const string CatalogFileName = "ps5_names.txt";

    // The catalog is ~150k lines; parse it once per file snapshot instead of on every
    // compilation start (the IDE creates one per keystroke). A changed file arrives as
    // a fresh AdditionalText instance, which naturally misses the cache.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AdditionalText, CatalogHolder> _catalogCache = new();

    private sealed class CatalogHolder
    {
        public CatalogHolder(HashSet<string>? names) => Names = names;

        public HashSet<string>? Names { get; }
    }

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
    [
        SysAbiDiagnostics.DuplicateNid,
        SysAbiDiagnostics.InvalidNidFormat,
        SysAbiDiagnostics.InvalidHandlerSignature,
        SysAbiDiagnostics.NidNameMismatch,
        SysAbiDiagnostics.UnresolvableExport,
        SysAbiDiagnostics.NameNotInCatalog,
        SysAbiDiagnostics.HandlerNotAccessible,
        SysAbiDiagnostics.InvalidGuestCString,
        SysAbiDiagnostics.ReturnValueOverwritten,
    ];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(static startContext =>
        {
            var catalogNames = LoadCatalog(startContext.Options.AdditionalFiles, startContext.CancellationToken);
            var exportsByNid = new ConcurrentDictionary<string, IMethodSymbol>();

            startContext.RegisterSymbolAction(
                symbolContext => AnalyzeMethod(symbolContext, catalogNames, exportsByNid),
                SymbolKind.Method);
            startContext.RegisterSyntaxNodeAction(
                AnalyzeReturnChannel,
                SyntaxKind.MethodDeclaration);
        });
    }

    private static HashSet<string>? LoadCatalog(
        ImmutableArray<AdditionalText> additionalFiles,
        System.Threading.CancellationToken cancellationToken)
    {
        foreach (var file in additionalFiles)
        {
            if (!file.Path.EndsWith(CatalogFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return _catalogCache.GetValue(file, static f => new CatalogHolder(ParseCatalog(f))).Names;
        }

        return null;
    }

    private static HashSet<string>? ParseCatalog(AdditionalText file)
    {
        var text = file.GetText();
        if (text is null)
        {
            return null;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in text.Lines)
        {
            var name = line.ToString().Trim();
            if (name.Length != 0)
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static void AnalyzeMethod(
        SymbolAnalysisContext context,
        HashSet<string>? catalogNames,
        ConcurrentDictionary<string, IMethodSymbol> exportsByNid)
    {
        var method = (IMethodSymbol)context.Symbol;
        AttributeData? exportAttribute = null;
        foreach (var attribute in method.GetAttributes())
        {
            if (SysAbiExportShape.IsSysAbiExportAttribute(attribute.AttributeClass))
            {
                exportAttribute = attribute;
                break;
            }
        }

        if (exportAttribute is null)
        {
            return;
        }

        var location = method.Locations.Length != 0 ? method.Locations[0] : Location.None;
        var methodDisplay = $"{method.ContainingType.ToDisplayString()}.{method.Name}";

        if (SysAbiExportShape.Classify(method, out _, out var invalidGuestCString) == SysAbiExportShape.HandlerShape.Invalid)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                invalidGuestCString ? SysAbiDiagnostics.InvalidGuestCString : SysAbiDiagnostics.InvalidHandlerSignature,
                location,
                methodDisplay));
            return;
        }

        if (!SysAbiExportShape.IsAccessibleFromGeneratedCode(method))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                SysAbiDiagnostics.HandlerNotAccessible, location, methodDisplay));
        }

        var arguments = SysAbiExportShape.ReadArguments(exportAttribute);
        var hasNid = !string.IsNullOrWhiteSpace(arguments.Nid);
        var hasName = !string.IsNullOrWhiteSpace(arguments.ExportName);

        if (!hasNid && !hasName)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                SysAbiDiagnostics.UnresolvableExport, location, methodDisplay));
            return;
        }

        if (hasNid && !Ps5Nid.IsValidFormat(arguments.Nid))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                SysAbiDiagnostics.InvalidNidFormat, location, arguments.Nid));
            return;
        }

        var effectiveNid = arguments.Nid;
        if (hasName)
        {
            var computed = Ps5Nid.Compute(arguments.ExportName);

            var nameInCatalog = catalogNames is not null && catalogNames.Contains(arguments.ExportName);

            // A declared NID that contradicts its name is only provably wrong when the
            // name is a real catalog symbol. Names outside the catalog are synthetic
            // labels for NIDs whose true symbol is unknown (the "sceAgcUnknown..."
            // convention) — for those the NID is authoritative and only SHEM006 applies.
            // With no catalog wired, every name is validated (fail closed).
            var nameIsKnown = catalogNames is null || nameInCatalog;
            if (hasNid && nameIsKnown && !string.Equals(computed, arguments.Nid, StringComparison.Ordinal))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    SysAbiDiagnostics.NidNameMismatch,
                    location,
                    arguments.Nid,
                    arguments.ExportName,
                    computed));
            }

            if (!hasNid)
            {
                effectiveNid = computed;
            }

            if (catalogNames is not null && !nameInCatalog)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    SysAbiDiagnostics.NameNotInCatalog, location, arguments.ExportName));
            }
        }

        var existing = exportsByNid.GetOrAdd(effectiveNid, method);
        if (!SymbolEqualityComparer.Default.Equals(existing, method))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                SysAbiDiagnostics.DuplicateNid,
                location,
                effectiveNid,
                $"{existing.ContainingType.ToDisplayString()}.{existing.Name}",
                methodDisplay));
        }
    }

    private static void AnalyzeReturnChannel(SyntaxNodeAnalysisContext context)
    {
        var declaration = (MethodDeclarationSyntax)context.Node;
        if (declaration.Body is null ||
            declaration.AttributeLists.Count == 0 ||
            context.SemanticModel.GetDeclaredSymbol(declaration, context.CancellationToken) is not IMethodSymbol method)
        {
            return;
        }

        var isExport = false;
        foreach (var attribute in method.GetAttributes())
        {
            if (SysAbiExportShape.IsSysAbiExportAttribute(attribute.AttributeClass))
            {
                isExport = true;
                break;
            }
        }

        if (!isExport)
        {
            return;
        }

        var methodDisplay = $"{method.ContainingType.ToDisplayString()}.{method.Name}";
        foreach (var returnStatement in declaration.Body.DescendantNodes().OfType<ReturnStatementSyntax>())
        {
            if (IsInsideNestedFunction(returnStatement, declaration) ||
                returnStatement.Expression is not InvocationExpressionSyntax invocation ||
                !TryGetCpuContextReceiver(context.SemanticModel, invocation, out var returnContext) ||
                returnStatement.Parent is not BlockSyntax block)
            {
                continue;
            }

            var returnIndex = block.Statements.IndexOf(returnStatement);
            for (var index = returnIndex - 1; index >= 0; index--)
            {
                var statement = block.Statements[index];
                if (statement is ExpressionStatementSyntax
                    {
                        Expression: AssignmentExpressionSyntax assignment
                    })
                {
                    if (WritesRax(context.SemanticModel, assignment, returnContext))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            SysAbiDiagnostics.ReturnValueOverwritten,
                            invocation.GetLocation(),
                            methodDisplay));
                        break;
                    }

                    continue;
                }

                if (statement is LocalDeclarationStatementSyntax or EmptyStatementSyntax)
                {
                    continue;
                }

                // Control flow requires full path-sensitive dataflow. Stop at that
                // boundary so the rule remains conservative and false-positive free.
                break;
            }
        }
    }

    private static bool IsInsideNestedFunction(
        ReturnStatementSyntax returnStatement,
        MethodDeclarationSyntax declaration)
    {
        for (var node = returnStatement.Parent; node is not null && node != declaration; node = node.Parent)
        {
            if (node is LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetCpuContextReceiver(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        out ISymbol receiver)
    {
        receiver = null!;
        if (semanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol
            {
                Name: "SetReturn",
                ContainingType: { Name: "CpuContext" } containingType
            } ||
            containingType.ContainingNamespace.ToDisplayString() != "SharpEmu.HLE" ||
            invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return false;
        }

        receiver = semanticModel.GetSymbolInfo(memberAccess.Expression).Symbol!;
        return receiver is not null;
    }

    private static bool WritesRax(
        SemanticModel semanticModel,
        AssignmentExpressionSyntax assignment,
        ISymbol returnContext)
    {
        if (assignment.Left is not ElementAccessExpressionSyntax elementAccess ||
            semanticModel.GetSymbolInfo(elementAccess).Symbol is not IPropertySymbol
            {
                IsIndexer: true,
                ContainingType: { Name: "CpuContext" } containingType
            } ||
            containingType.ContainingNamespace.ToDisplayString() != "SharpEmu.HLE" ||
            elementAccess.ArgumentList.Arguments.Count != 1 ||
            semanticModel.GetSymbolInfo(elementAccess.ArgumentList.Arguments[0].Expression).Symbol is not IFieldSymbol
            {
                Name: "Rax",
                ContainingType: { Name: "CpuRegister" } registerType
            } ||
            registerType.ContainingNamespace.ToDisplayString() != "SharpEmu.HLE")
        {
            return false;
        }

        var writeContext = semanticModel.GetSymbolInfo(elementAccess.Expression).Symbol;
        return SymbolEqualityComparer.Default.Equals(writeContext, returnContext);
    }
}
