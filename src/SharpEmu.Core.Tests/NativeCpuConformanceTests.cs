// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using Xunit;

namespace SharpEmu.Core.Tests;

public sealed class NativeCpuConformanceTests
{
    public static TheoryData<string, byte[]> InstructionSequences => new()
    {
        {
            "return-zero",
            [
                0x31, 0xC0, // xor eax, eax
                0xC3,       // ret
            ]
        },
        {
            "overflow-flag",
            [
                0xB8, 0xFF, 0xFF, 0xFF, 0x7F, // mov eax, 0x7fffffff
                0x83, 0xC0, 0x01,             // add eax, 1
                0x70, 0x06,                   // jo success
                0xB8, 0x01, 0x00, 0x00, 0x00, // mov eax, 1
                0xC3,                         // ret
                0x31, 0xC0,                   // success: xor eax, eax
                0xC3,                         // ret
            ]
        },
        {
            "nested-call",
            [
                0xE8, 0x0D, 0x00, 0x00, 0x00, // call helper
                0x85, 0xC0,                   // test eax, eax
                0x75, 0x03,                   // jne failure
                0x31, 0xC0,                   // xor eax, eax
                0xC3,                         // ret
                0xB8, 0x01, 0x00, 0x00, 0x00, // failure: mov eax, 1
                0xC3,                         // ret
                0x31, 0xC0,                   // helper: xor eax, eax
                0xC3,                         // ret
            ]
        },
        {
            "tls-preserves-registers-and-flags",
            [
                0xB9, 0x44, 0x33, 0x22, 0x11,       // mov ecx, 0x11223344
                0x81, 0xF9, 0x44, 0x33, 0x22, 0x11, // cmp ecx, 0x11223344
                0x64, 0x48, 0x8B, 0x04, 0x25,
                0x00, 0x00, 0x00, 0x00,             // mov rax, fs:[0]
                0x75, 0x0B,                         // jne failure
                0x81, 0xF9, 0x44, 0x33, 0x22, 0x11, // cmp ecx, 0x11223344
                0x75, 0x03,                         // jne failure
                0x31, 0xC0,                         // xor eax, eax
                0xC3,                               // ret
                0xB8, 0x01, 0x00, 0x00, 0x00,       // failure: mov eax, 1
                0xC3,                               // ret
            ]
        },
        {
            "tls-load-preserves-rax-for-other-destinations",
            [
                0x48, 0xB8, 0x88, 0x77, 0x66, 0x55,
                0x44, 0x33, 0x22, 0x11,             // mov rax, 0x1122334455667788
                0x64, 0x48, 0x8B, 0x14, 0x25,
                0x00, 0x00, 0x00, 0x00,             // mov rdx, fs:[0]
                0x48, 0xB9, 0x88, 0x77, 0x66, 0x55,
                0x44, 0x33, 0x22, 0x11,             // mov rcx, 0x1122334455667788
                0x48, 0x39, 0xC8,                   // cmp rax, rcx
                0x75, 0x08,                         // jne failure
                0x48, 0x85, 0xD2,                   // test rdx, rdx
                0x74, 0x03,                         // jz failure
                0x31, 0xC0,                         // xor eax, eax
                0xC3,                               // ret
                0xB8, 0x01, 0x00, 0x00, 0x00,       // failure: mov eax, 1
                0xC3,                               // ret
            ]
        },
        {
            "tls-load-supports-nonvolatile-destinations",
            [
                0x48, 0xB8, 0x88, 0x77, 0x66, 0x55,
                0x44, 0x33, 0x22, 0x11,             // mov rax, 0x1122334455667788
                0x64, 0x48, 0x8B, 0x1C, 0x25,
                0x00, 0x00, 0x00, 0x00,             // mov rbx, fs:[0]
                0x48, 0xB9, 0x88, 0x77, 0x66, 0x55,
                0x44, 0x33, 0x22, 0x11,             // mov rcx, 0x1122334455667788
                0x48, 0x39, 0xC8,                   // cmp rax, rcx
                0x75, 0x08,                         // jne failure
                0x48, 0x85, 0xDB,                   // test rbx, rbx
                0x74, 0x03,                         // jz failure
                0x31, 0xC0,                         // xor eax, eax
                0xC3,                               // ret
                0xB8, 0x01, 0x00, 0x00, 0x00,       // failure: mov eax, 1
                0xC3,                               // ret
            ]
        },
        {
            "tls-load-supports-redundant-prefixes",
            [
                0xF3, 0x64, 0x48, 0x8B, 0x04, 0x25,
                0x00, 0x00, 0x00, 0x00,             // mov rax, fs:[0]
                0x48, 0x85, 0xC0,                   // test rax, rax
                0x74, 0x03,                         // jz failure
                0x31, 0xC0,                         // xor eax, eax
                0xC3,                               // ret
                0xB8, 0x01, 0x00, 0x00, 0x00,       // failure: mov eax, 1
                0xC3,                               // ret
            ]
        },
        {
            "tls-load-supports-extended-volatile-destinations",
            [
                0x48, 0xB8, 0x88, 0x77, 0x66, 0x55,
                0x44, 0x33, 0x22, 0x11,             // mov rax, 0x1122334455667788
                0x64, 0x4C, 0x8B, 0x04, 0x25,
                0x00, 0x00, 0x00, 0x00,             // mov r8, fs:[0]
                0x48, 0xB9, 0x88, 0x77, 0x66, 0x55,
                0x44, 0x33, 0x22, 0x11,             // mov rcx, 0x1122334455667788
                0x48, 0x39, 0xC8,                   // cmp rax, rcx
                0x75, 0x08,                         // jne failure
                0x4D, 0x85, 0xC0,                   // test r8, r8
                0x74, 0x03,                         // jz failure
                0x31, 0xC0,                         // xor eax, eax
                0xC3,                               // ret
                0xB8, 0x01, 0x00, 0x00, 0x00,       // failure: mov eax, 1
                0xC3,                               // ret
            ]
        },
        {
            "tls-load-supports-extended-nonvolatile-destinations",
            [
                0x48, 0xB8, 0x88, 0x77, 0x66, 0x55,
                0x44, 0x33, 0x22, 0x11,             // mov rax, 0x1122334455667788
                0x64, 0x4C, 0x8B, 0x3C, 0x25,
                0x00, 0x00, 0x00, 0x00,             // mov r15, fs:[0]
                0x48, 0xB9, 0x88, 0x77, 0x66, 0x55,
                0x44, 0x33, 0x22, 0x11,             // mov rcx, 0x1122334455667788
                0x48, 0x39, 0xC8,                   // cmp rax, rcx
                0x75, 0x08,                         // jne failure
                0x4D, 0x85, 0xFF,                   // test r15, r15
                0x74, 0x03,                         // jz failure
                0x31, 0xC0,                         // xor eax, eax
                0xC3,                               // ret
                0xB8, 0x01, 0x00, 0x00, 0x00,       // failure: mov eax, 1
                0xC3,                               // ret
            ]
        },
        {
            "tls-load-preserves-arithmetic-flags",
            [
                0xB8, 0xFF, 0xFF, 0xFF, 0x7F,       // mov eax, 0x7fffffff
                0x83, 0xC0, 0x01,                   // add eax, 1
                0x64, 0x48, 0x8B, 0x14, 0x25,
                0x00, 0x00, 0x00, 0x00,             // mov rdx, fs:[0]
                0x71, 0x0E,                         // jno failure
                0x79, 0x0C,                         // jns failure
                0x74, 0x0A,                         // jz failure
                0x72, 0x08,                         // jc failure
                0x48, 0x85, 0xD2,                   // test rdx, rdx
                0x74, 0x03,                         // jz failure
                0x31, 0xC0,                         // xor eax, eax
                0xC3,                               // ret
                0xB8, 0x01, 0x00, 0x00, 0x00,       // failure: mov eax, 1
                0xC3,                               // ret
            ]
        },
        {
            "tls-load-supports-positive-offset",
            [
                0x64, 0x48, 0x8B, 0x04, 0x25,
                0x10, 0x00, 0x00, 0x00,             // mov rax, fs:[0x10]
                0x64, 0x48, 0x8B, 0x14, 0x25,
                0x00, 0x00, 0x00, 0x00,             // mov rdx, fs:[0]
                0x48, 0x39, 0xD0,                   // cmp rax, rdx
                0x75, 0x03,                         // jne failure
                0x31, 0xC0,                         // xor eax, eax
                0xC3,                               // ret
                0xB8, 0x01, 0x00, 0x00, 0x00,       // failure: mov eax, 1
                0xC3,                               // ret
            ]
        },
        {
            "tls-load-reads-seeded-field",
            [
                0x64, 0x48, 0x8B, 0x04, 0x25,
                0x00, 0x00, 0x00, 0x00,             // mov rax, fs:[0]
                0x64, 0x48, 0x8B, 0x04, 0x25,
                0x28, 0x00, 0x00, 0x00,             // mov rax, fs:[0x28]
                0x48, 0xB9, 0xBE, 0xBA, 0xFE, 0xCA,
                0xDE, 0xC0, 0xDE, 0xC0,             // mov rcx, 0xC0DEC0DECAFEBABE
                0x48, 0x39, 0xC8,                   // cmp rax, rcx
                0x75, 0x03,                         // jne failure
                0x31, 0xC0,                         // xor eax, eax
                0xC3,                               // ret
                0xB8, 0x01, 0x00, 0x00, 0x00,       // failure: mov eax, 1
                0xC3,                               // ret
            ]
        },
        {
            "tls-load-zero-extends-byte-into-extended-register",
            [
                0x64, 0xC7, 0x04, 0x25,
                0x74, 0x00, 0x00, 0x00,
                0x80, 0xFF, 0xFF, 0xFF,             // mov dword fs:[0x74], 0xFFFFFF80
                0x64, 0x44, 0x0F, 0xB6, 0x04, 0x25,
                0x74, 0x00, 0x00, 0x00,             // movzx r8d, byte fs:[0x74]
                0x41, 0x81, 0xF8, 0x80, 0x00, 0x00, 0x00, // cmp r8d, 0x80
                0x75, 0x03,                         // jne failure
                0x31, 0xC0,                         // xor eax, eax
                0xC3,                               // ret
                0xB8, 0x01, 0x00, 0x00, 0x00,       // failure: mov eax, 1
                0xC3,                               // ret
            ]
        },
        {
            "tls-load-sign-extends-byte-into-qword",
            [
                0x64, 0xC7, 0x04, 0x25,
                0x78, 0x00, 0x00, 0x00,
                0x80, 0x00, 0x00, 0x00,             // mov dword fs:[0x78], 0x80
                0x64, 0x4C, 0x0F, 0xBE, 0x3C, 0x25,
                0x78, 0x00, 0x00, 0x00,             // movsx r15, byte fs:[0x78]
                0x49, 0x83, 0xFF, 0x80,             // cmp r15, -128
                0x75, 0x03,                         // jne failure
                0x31, 0xC0,                         // xor eax, eax
                0xC3,                               // ret
                0xB8, 0x01, 0x00, 0x00, 0x00,       // failure: mov eax, 1
                0xC3,                               // ret
            ]
        },
        {
            "tls-load-zero-extends-word-into-dword",
            [
                0x64, 0xC7, 0x04, 0x25,
                0x7C, 0x00, 0x00, 0x00,
                0x01, 0x80, 0xFF, 0xFF,             // mov dword fs:[0x7C], 0xFFFF8001
                0x64, 0x0F, 0xB7, 0x04, 0x25,
                0x7C, 0x00, 0x00, 0x00,             // movzx eax, word fs:[0x7C]
                0x3D, 0x01, 0x80, 0x00, 0x00,       // cmp eax, 0x8001
                0x75, 0x03,                         // jne failure
                0x31, 0xC0,                         // xor eax, eax
                0xC3,                               // ret
                0xB8, 0x01, 0x00, 0x00, 0x00,       // failure: mov eax, 1
                0xC3,                               // ret
            ]
        },
        {
            "tls-load-sign-extends-dword-into-qword",
            [
                0x64, 0xC7, 0x04, 0x25,
                0x80, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x80,             // mov dword fs:[0x80], 0x80000000
                0x64, 0x48, 0x63, 0x04, 0x25,
                0x80, 0x00, 0x00, 0x00,             // movsxd rax, dword fs:[0x80]
                0x48, 0xB9, 0x00, 0x00, 0x00, 0x80,
                0xFF, 0xFF, 0xFF, 0xFF,             // mov rcx, 0xFFFFFFFF80000000
                0x48, 0x39, 0xC8,                   // cmp rax, rcx
                0x75, 0x03,                         // jne failure
                0x31, 0xC0,                         // xor eax, eax
                0xC3,                               // ret
                0xB8, 0x01, 0x00, 0x00, 0x00,       // failure: mov eax, 1
                0xC3,                               // ret
            ]
        },
        {
            "tls-load-zero-extends-byte-into-qword",
            [
                0x64, 0xC7, 0x04, 0x25,
                0x84, 0x00, 0x00, 0x00,
                0x80, 0xFF, 0xFF, 0xFF,             // mov dword fs:[0x84], 0xFFFFFF80
                0x64, 0x48, 0x0F, 0xB6, 0x04, 0x25,
                0x84, 0x00, 0x00, 0x00,             // movzx rax, byte fs:[0x84]
                0x48, 0x3D, 0x80, 0x00, 0x00, 0x00, // cmp rax, 0x80
                0x75, 0x03,                         // jne failure
                0x31, 0xC0,                         // xor eax, eax
                0xC3,                               // ret
                0xB8, 0x01, 0x00, 0x00, 0x00,       // failure: mov eax, 1
                0xC3,                               // ret
            ]
        },
        {
            "tls-load-sign-extends-byte-into-dword",
            [
                0x64, 0xC7, 0x04, 0x25,
                0x88, 0x00, 0x00, 0x00,
                0x80, 0x00, 0x00, 0x00,             // mov dword fs:[0x88], 0x80
                0x64, 0x0F, 0xBE, 0x04, 0x25,
                0x88, 0x00, 0x00, 0x00,             // movsx eax, byte fs:[0x88]
                0x3D, 0x80, 0xFF, 0xFF, 0xFF,       // cmp eax, -128
                0x75, 0x03,                         // jne failure
                0x31, 0xC0,                         // xor eax, eax
                0xC3,                               // ret
                0xB8, 0x01, 0x00, 0x00, 0x00,       // failure: mov eax, 1
                0xC3,                               // ret
            ]
        },
        {
            "tls-load-zero-extends-word-into-qword",
            [
                0x64, 0xC7, 0x04, 0x25,
                0x8C, 0x00, 0x00, 0x00,
                0x01, 0x80, 0xFF, 0xFF,             // mov dword fs:[0x8C], 0xFFFF8001
                0x64, 0x48, 0x0F, 0xB7, 0x04, 0x25,
                0x8C, 0x00, 0x00, 0x00,             // movzx rax, word fs:[0x8C]
                0x48, 0x3D, 0x01, 0x80, 0x00, 0x00, // cmp rax, 0x8001
                0x75, 0x03,                         // jne failure
                0x31, 0xC0,                         // xor eax, eax
                0xC3,                               // ret
                0xB8, 0x01, 0x00, 0x00, 0x00,       // failure: mov eax, 1
                0xC3,                               // ret
            ]
        },
        {
            "tls-load-sign-extends-word-into-dword",
            [
                0x64, 0xC7, 0x04, 0x25,
                0x90, 0x00, 0x00, 0x00,
                0x01, 0x80, 0x00, 0x00,             // mov dword fs:[0x90], 0x8001
                0x64, 0x0F, 0xBF, 0x04, 0x25,
                0x90, 0x00, 0x00, 0x00,             // movsx eax, word fs:[0x90]
                0x3D, 0x01, 0x80, 0xFF, 0xFF,       // cmp eax, 0xFFFF8001
                0x75, 0x03,                         // jne failure
                0x31, 0xC0,                         // xor eax, eax
                0xC3,                               // ret
                0xB8, 0x01, 0x00, 0x00, 0x00,       // failure: mov eax, 1
                0xC3,                               // ret
            ]
        },
        {
            "tls-load-sign-extends-word-into-qword",
            [
                0x64, 0xC7, 0x04, 0x25,
                0x94, 0x00, 0x00, 0x00,
                0x01, 0x80, 0x00, 0x00,             // mov dword fs:[0x94], 0x8001
                0x64, 0x48, 0x0F, 0xBF, 0x04, 0x25,
                0x94, 0x00, 0x00, 0x00,             // movsx rax, word fs:[0x94]
                0x48, 0x3D, 0x01, 0x80, 0xFF, 0xFF, // cmp rax, -32767
                0x75, 0x03,                         // jne failure
                0x31, 0xC0,                         // xor eax, eax
                0xC3,                               // ret
                0xB8, 0x01, 0x00, 0x00, 0x00,       // failure: mov eax, 1
                0xC3,                               // ret
            ]
        },
        {
            "stack-canary-xor-uses-seeded-tls-value",
            [
                0x49, 0xBF, 0x88, 0x77, 0x66, 0x55,
                0x44, 0x33, 0x22, 0x11,             // mov r15, 0x1122334455667788
                0x64, 0x4C, 0x33, 0x3C, 0x25,
                0x28, 0x00, 0x00, 0x00,             // xor r15, fs:[0x28]
                0x48, 0xB8, 0x36, 0xCD, 0x98, 0x9F,
                0x9A, 0xF3, 0xFC, 0xD1,             // mov rax, 0xD1FCF39A9F98CD36
                0x49, 0x39, 0xC7,                   // cmp r15, rax
                0x75, 0x03,                         // jne failure
                0x31, 0xC0,                         // xor eax, eax
                0xC3,                               // ret
                0xB8, 0x01, 0x00, 0x00, 0x00,       // failure: mov eax, 1
                0xC3,                               // ret
            ]
        },
        {
            "stack-canary-xor-updates-zero-flag",
            [
                0x31, 0xC0,                         // xor eax, eax
                0x64, 0x48, 0x33, 0x04, 0x25,
                0x28, 0x00, 0x00, 0x00,             // xor rax, fs:[0x28]
                0x74, 0x03,                         // jz failure
                0x31, 0xC0,                         // xor eax, eax
                0xC3,                               // ret
                0xB8, 0x01, 0x00, 0x00, 0x00,       // failure: mov eax, 1
                0xC3,                               // ret
            ]
        },
        {
            "stack-canary-xor-supports-dword-destination",
            [
                0xB8, 0x44, 0x33, 0x22, 0x11,       // mov eax, 0x11223344
                0x64, 0x33, 0x04, 0x25,
                0x28, 0x00, 0x00, 0x00,             // xor eax, dword fs:[0x28]
                0x3D, 0xFA, 0x89, 0xDC, 0xDB,       // cmp eax, 0xDBDC89FA
                0x75, 0x03,                         // jne failure
                0x31, 0xC0,                         // xor eax, eax
                0xC3,                               // ret
                0xB8, 0x01, 0x00, 0x00, 0x00,       // failure: mov eax, 1
                0xC3,                               // ret
            ]
        },
        {
            "stack-canary-xor-supports-extended-volatile-destination",
            [
                0x45, 0x31, 0xC0,                   // xor r8d, r8d
                0x64, 0x4C, 0x33, 0x04, 0x25,
                0x28, 0x00, 0x00, 0x00,             // xor r8, fs:[0x28]
                0x48, 0xB8, 0xBE, 0xBA, 0xFE, 0xCA,
                0xDE, 0xC0, 0xDE, 0xC0,             // mov rax, 0xC0DEC0DECAFEBABE
                0x49, 0x39, 0xC0,                   // cmp r8, rax
                0x75, 0x03,                         // jne failure
                0x31, 0xC0,                         // xor eax, eax
                0xC3,                               // ret
                0xB8, 0x01, 0x00, 0x00, 0x00,       // failure: mov eax, 1
                0xC3,                               // ret
            ]
        },
        {
            "stack-canary-sub-uses-seeded-tls-value-and-flags",
            [
                0x49, 0xBF, 0xBF, 0xBA, 0xFE, 0xCA,
                0xDE, 0xC0, 0xDE, 0xC0,             // mov r15, 0xC0DEC0DECAFEBABF
                0x64, 0x4C, 0x2B, 0x3C, 0x25,
                0x28, 0x00, 0x00, 0x00,             // sub r15, fs:[0x28]
                0x74, 0x09,                         // jz failure
                0x49, 0x83, 0xFF, 0x01,             // cmp r15, 1
                0x75, 0x03,                         // jne failure
                0x31, 0xC0,                         // xor eax, eax
                0xC3,                               // ret
                0xB8, 0x01, 0x00, 0x00, 0x00,       // failure: mov eax, 1
                0xC3,                               // ret
            ]
        },
        {
            "stack-canary-arithmetic-keeps-distinct-helpers",
            [
                0x48, 0xB8, 0xBE, 0xBA, 0xFE, 0xCA,
                0xDE, 0xC0, 0xDE, 0xC0,             // mov rax, 0xC0DEC0DECAFEBABE
                0x64, 0x48, 0x33, 0x04, 0x25,
                0x28, 0x00, 0x00, 0x00,             // xor rax, fs:[0x28]
                0x75, 0x1C,                         // jne failure
                0x48, 0xB8, 0xC0, 0xBA, 0xFE, 0xCA,
                0xDE, 0xC0, 0xDE, 0xC0,             // mov rax, 0xC0DEC0DECAFEBAC0
                0x64, 0x48, 0x2B, 0x04, 0x25,
                0x28, 0x00, 0x00, 0x00,             // sub rax, fs:[0x28]
                0x48, 0x83, 0xF8, 0x02,             // cmp rax, 2
                0x75, 0x03,                         // jne failure
                0x31, 0xC0,                         // xor eax, eax
                0xC3,                               // ret
                0xB8, 0x01, 0x00, 0x00, 0x00,       // failure: mov eax, 1
                0xC3,                               // ret
            ]
        },
        {
            "tls-register-store-roundtrips-volatile-source",
            [
                0x48, 0xB8, 0x88, 0x77, 0x66, 0x55,
                0x44, 0x33, 0x22, 0x11,             // mov rax, 0x1122334455667788
                0x64, 0x48, 0x89, 0x04, 0x25,
                0x38, 0x00, 0x00, 0x00,             // mov fs:[0x38], rax
                0x31, 0xC0,                         // xor eax, eax
                0x64, 0x48, 0x8B, 0x04, 0x25,
                0x38, 0x00, 0x00, 0x00,             // mov rax, fs:[0x38]
                0x48, 0xB9, 0x88, 0x77, 0x66, 0x55,
                0x44, 0x33, 0x22, 0x11,             // mov rcx, 0x1122334455667788
                0x48, 0x39, 0xC8,                   // cmp rax, rcx
                0x75, 0x03,                         // jne failure
                0x31, 0xC0,                         // xor eax, eax
                0xC3,                               // ret
                0xB8, 0x01, 0x00, 0x00, 0x00,       // failure: mov eax, 1
                0xC3,                               // ret
            ]
        },
        {
            "tls-register-store-preserves-nonvolatile-source",
            [
                0x49, 0xBF, 0x11, 0x22, 0x33, 0x44,
                0x55, 0x66, 0x77, 0x08,             // mov r15, 0x0877665544332211
                0x64, 0x4C, 0x89, 0x3C, 0x25,
                0x40, 0x00, 0x00, 0x00,             // mov fs:[0x40], r15
                0x64, 0x48, 0x8B, 0x04, 0x25,
                0x40, 0x00, 0x00, 0x00,             // mov rax, fs:[0x40]
                0x49, 0xBA, 0x11, 0x22, 0x33, 0x44,
                0x55, 0x66, 0x77, 0x08,             // mov r10, 0x0877665544332211
                0x4C, 0x39, 0xD0,                   // cmp rax, r10
                0x75, 0x08,                         // jne failure
                0x4D, 0x39, 0xD7,                   // cmp r15, r10
                0x75, 0x03,                         // jne failure
                0x31, 0xC0,                         // xor eax, eax
                0xC3,                               // ret
                0xB8, 0x01, 0x00, 0x00, 0x00,       // failure: mov eax, 1
                0xC3,                               // ret
            ]
        },
        {
            "tls-register-store-preserves-arithmetic-flags",
            [
                0x48, 0xBA, 0x88, 0x77, 0x66, 0x55,
                0x44, 0x33, 0x22, 0x11,             // mov rdx, 0x1122334455667788
                0xB8, 0xFF, 0xFF, 0xFF, 0x7F,       // mov eax, 0x7fffffff
                0x83, 0xC0, 0x01,                   // add eax, 1
                0x64, 0x48, 0x89, 0x14, 0x25,
                0x48, 0x00, 0x00, 0x00,             // mov fs:[0x48], rdx
                0x71, 0x09,                         // jno failure
                0x79, 0x07,                         // jns failure
                0x74, 0x05,                         // jz failure
                0x72, 0x03,                         // jc failure
                0x31, 0xC0,                         // xor eax, eax
                0xC3,                               // ret
                0xB8, 0x01, 0x00, 0x00, 0x00,       // failure: mov eax, 1
                0xC3,                               // ret
            ]
        },
        {
            "tls-register-store-captures-guest-stack-pointer",
            [
                0x64, 0x48, 0x89, 0x24, 0x25,
                0x50, 0x00, 0x00, 0x00,             // mov fs:[0x50], rsp
                0x64, 0x48, 0x8B, 0x04, 0x25,
                0x50, 0x00, 0x00, 0x00,             // mov rax, fs:[0x50]
                0x48, 0x39, 0xE0,                   // cmp rax, rsp
                0x75, 0x03,                         // jne failure
                0x31, 0xC0,                         // xor eax, eax
                0xC3,                               // ret
                0xB8, 0x01, 0x00, 0x00, 0x00,       // failure: mov eax, 1
                0xC3,                               // ret
            ]
        },
        {
            "tls-32-bit-load-zero-extends-destination",
            [
                0x48, 0xB8, 0xFF, 0xFF, 0xFF, 0xFF,
                0xFF, 0xFF, 0xFF, 0xFF,             // mov rax, -1
                0x64, 0x8B, 0x04, 0x25,
                0x28, 0x00, 0x00, 0x00,             // mov eax, fs:[0x28]
                0x48, 0xB9, 0xBE, 0xBA, 0xFE, 0xCA,
                0x00, 0x00, 0x00, 0x00,             // mov rcx, 0x00000000CAFEBABE
                0x48, 0x39, 0xC8,                   // cmp rax, rcx
                0x75, 0x03,                         // jne failure
                0x31, 0xC0,                         // xor eax, eax
                0xC3,                               // ret
                0xB8, 0x01, 0x00, 0x00, 0x00,       // failure: mov eax, 1
                0xC3,                               // ret
            ]
        },
        {
            "tls-32-bit-register-store-roundtrips-value",
            [
                0x48, 0xB8, 0x44, 0x33, 0x22, 0x11,
                0xFF, 0xFF, 0xFF, 0xFF,             // mov rax, 0xFFFFFFFF11223344
                0x64, 0x89, 0x04, 0x25,
                0x58, 0x00, 0x00, 0x00,             // mov fs:[0x58], eax
                0x48, 0xC7, 0xC0, 0xFF, 0xFF, 0xFF, 0xFF, // mov rax, -1
                0x64, 0x8B, 0x04, 0x25,
                0x58, 0x00, 0x00, 0x00,             // mov eax, fs:[0x58]
                0x48, 0xB9, 0x44, 0x33, 0x22, 0x11,
                0x00, 0x00, 0x00, 0x00,             // mov rcx, 0x0000000011223344
                0x48, 0x39, 0xC8,                   // cmp rax, rcx
                0x75, 0x03,                         // jne failure
                0x31, 0xC0,                         // xor eax, eax
                0xC3,                               // ret
                0xB8, 0x01, 0x00, 0x00, 0x00,       // failure: mov eax, 1
                0xC3,                               // ret
            ]
        },
        {
            "tls-32-bit-register-store-supports-extended-source",
            [
                0x49, 0xBF, 0x11, 0x22, 0x33, 0x44,
                0x55, 0x66, 0x77, 0x08,             // mov r15, 0x0877665544332211
                0x64, 0x44, 0x89, 0x3C, 0x25,
                0x5C, 0x00, 0x00, 0x00,             // mov fs:[0x5C], r15d
                0x64, 0x44, 0x8B, 0x04, 0x25,
                0x5C, 0x00, 0x00, 0x00,             // mov r8d, fs:[0x5C]
                0x41, 0x81, 0xF8, 0x11, 0x22, 0x33, 0x44, // cmp r8d, 0x44332211
                0x75, 0x12,                         // jne failure
                0x49, 0xBA, 0x11, 0x22, 0x33, 0x44,
                0x55, 0x66, 0x77, 0x08,             // mov r10, 0x0877665544332211
                0x4D, 0x39, 0xD7,                   // cmp r15, r10
                0x75, 0x03,                         // jne failure
                0x31, 0xC0,                         // xor eax, eax
                0xC3,                               // ret
                0xB8, 0x01, 0x00, 0x00, 0x00,       // failure: mov eax, 1
                0xC3,                               // ret
            ]
        },
        {
            "tls-32-bit-immediate-store-preserves-register-and-adjacent-bytes",
            [
                0x48, 0xB8, 0x00, 0x00, 0x00, 0x00,
                0xDD, 0xCC, 0xBB, 0xAA,             // mov rax, 0xAABBCCDD00000000
                0x64, 0x48, 0x89, 0x04, 0x25,
                0x60, 0x00, 0x00, 0x00,             // mov fs:[0x60], rax
                0x64, 0xC7, 0x04, 0x25,
                0x60, 0x00, 0x00, 0x00,
                0x78, 0x56, 0x34, 0x12,             // mov dword fs:[0x60], 0x12345678
                0x48, 0xB9, 0x00, 0x00, 0x00, 0x00,
                0xDD, 0xCC, 0xBB, 0xAA,             // mov rcx, 0xAABBCCDD00000000
                0x48, 0x39, 0xC8,                   // cmp rax, rcx
                0x75, 0x1B,                         // jne failure
                0x64, 0x48, 0x8B, 0x04, 0x25,
                0x60, 0x00, 0x00, 0x00,             // mov rax, fs:[0x60]
                0x48, 0xB9, 0x78, 0x56, 0x34, 0x12,
                0xDD, 0xCC, 0xBB, 0xAA,             // mov rcx, 0xAABBCCDD12345678
                0x48, 0x39, 0xC8,                   // cmp rax, rcx
                0x75, 0x03,                         // jne failure
                0x31, 0xC0,                         // xor eax, eax
                0xC3,                               // ret
                0xB8, 0x01, 0x00, 0x00, 0x00,       // failure: mov eax, 1
                0xC3,                               // ret
            ]
        },
        {
            "tls-64-bit-immediate-store-sign-extends-value",
            [
                0x64, 0x48, 0xC7, 0x04, 0x25,
                0x68, 0x00, 0x00, 0x00,
                0x80, 0xFF, 0xFF, 0xFF,             // mov qword fs:[0x68], -128
                0x64, 0x48, 0x8B, 0x04, 0x25,
                0x68, 0x00, 0x00, 0x00,             // mov rax, fs:[0x68]
                0x48, 0x83, 0xF8, 0x80,             // cmp rax, -128
                0x75, 0x03,                         // jne failure
                0x31, 0xC0,                         // xor eax, eax
                0xC3,                               // ret
                0xB8, 0x01, 0x00, 0x00, 0x00,       // failure: mov eax, 1
                0xC3,                               // ret
            ]
        },
        {
            "tls-immediate-store-preserves-arithmetic-flags",
            [
                0xB8, 0xFF, 0xFF, 0xFF, 0x7F,       // mov eax, 0x7fffffff
                0x83, 0xC0, 0x01,                   // add eax, 1
                0x64, 0xC7, 0x04, 0x25,
                0x70, 0x00, 0x00, 0x00,
                0x78, 0x56, 0x34, 0x12,             // mov dword fs:[0x70], 0x12345678
                0x71, 0x09,                         // jno failure
                0x79, 0x07,                         // jns failure
                0x74, 0x05,                         // jz failure
                0x72, 0x03,                         // jc failure
                0x31, 0xC0,                         // xor eax, eax
                0xC3,                               // ret
                0xB8, 0x01, 0x00, 0x00, 0x00,       // failure: mov eax, 1
                0xC3,                               // ret
            ]
        },
        {
            "tls-load-inside-nested-call",
            [
                0xE8, 0x0D, 0x00, 0x00, 0x00,       // call helper
                0x85, 0xC0,                         // test eax, eax
                0x75, 0x03,                         // jne failure
                0x31, 0xC0,                         // xor eax, eax
                0xC3,                               // ret
                0xB8, 0x01, 0x00, 0x00, 0x00,       // failure: mov eax, 1
                0xC3,                               // ret
                0x64, 0x48, 0x8B, 0x14, 0x25,
                0x00, 0x00, 0x00, 0x00,             // helper: mov rdx, fs:[0]
                0x48, 0x85, 0xD2,                   // test rdx, rdx
                0x74, 0x03,                         // jz helperFailure
                0x31, 0xC0,                         // xor eax, eax
                0xC3,                               // ret
                0xB8, 0x01, 0x00, 0x00, 0x00,       // helperFailure: mov eax, 1
                0xC3,                               // ret
            ]
        },
    };

    [WindowsX64Theory]
    [MemberData(nameof(InstructionSequences))]
    public async Task ExecutesNativeInstructionSequence(string name, byte[] code)
    {
        var execution = await SyntheticCliGuest.RunAsync(
            code,
            requestReport: true,
            executionTimeoutSeconds: 10);

        Assert.True(
            execution.ExitCode == 0,
            $"Conformance case '{name}' exited with {execution.ExitCode}.{Environment.NewLine}" +
            $"stdout:{Environment.NewLine}{execution.StandardOutput}{Environment.NewLine}" +
            $"stderr:{Environment.NewLine}{execution.StandardError}");
        Assert.NotNull(execution.ReportJson);
        using var report = JsonDocument.Parse(execution.ReportJson);
        Assert.Equal(
            "ORBIS_GEN2_OK",
            report.RootElement.GetProperty("result").GetProperty("name").GetString());
    }

    [WindowsX64Fact]
    public async Task RepeatedModuleInitializersShareNativeExecutionSession()
    {
        var execution = await SyntheticCliGuest.RunAsync(
            [0x31, 0xC0, 0xC3], // main: xor eax, eax; ret
            requestReport: true,
            executionTimeoutSeconds: 10,
            adjacentModuleImage: SyntheticElfImage.CreateModuleWithInitializerArray(
                [0xC3], // DT_INIT: ret
                [0xC3], // repeated DT_INIT_ARRAY target: ret
                arrayInitializerCount: 23));

        Assert.True(
            execution.ExitCode == 0,
            $"Repeated initializer session exited with {execution.ExitCode}.{Environment.NewLine}" +
            $"stdout:{Environment.NewLine}{execution.StandardOutput}{Environment.NewLine}" +
            $"stderr:{Environment.NewLine}{execution.StandardError}");
        Assert.NotNull(execution.ReportJson);
        using var report = JsonDocument.Parse(execution.ReportJson);
        var executions = report.RootElement.GetProperty("moduleInitializers").EnumerateArray().ToArray();
        Assert.Equal(24, executions.Length);
        for (var index = 0; index < executions.Length; index++)
        {
            Assert.Equal(index, executions[index].GetProperty("index").GetInt32());
            Assert.Equal(
                "ORBIS_GEN2_OK",
                executions[index].GetProperty("result").GetProperty("name").GetString());
        }
    }
}
