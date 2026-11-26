using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace SpanForge.Generators;

[Generator(LanguageNames.CSharp)]
public sealed class BinaryUnionGenerator : IIncrementalGenerator
{
    private const string BinaryStructAttr = "SpanForge.BinaryStructAttribute";
    private const string BinaryUnionAttr = "SpanForge.BinaryUnionAttribute";
    private const string UnionCaseAttr = "SpanForge.UnionCaseAttribute";
    private const string MagicAttr = "SpanForge.MagicAttribute";

    private static readonly DiagnosticDescriptor InvalidUnionDiagnostic = new(
        id: "SFG004",
        title: "Invalid BinaryUnion configuration",
        messageFormat: "Invalid [BinaryUnion] configuration on '{0}': {1}",
        category: "SpanForge.Generators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedUnionPayloadDiagnostic = new(
        id: "SFG005",
        title: "Unsupported UnionCase payload type",
        messageFormat: "Unsupported [UnionCase] payload type '{1}' on '{0}'; use a primitive numeric type, bool, [BinaryStruct], or [BinaryUnion]",
        category: "SpanForge.Generators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<UnionGenerationResult> pipeline = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                fullyQualifiedMetadataName: BinaryUnionAttr,
                predicate: static (node, _) => node is TypeDeclarationSyntax,
                transform: static (ctx, _) => Transform(ctx));

        context.RegisterSourceOutput(pipeline, static (spc, result) =>
        {
            foreach (DiagnosticInfo diag in result.Diagnostics)
            {
                spc.ReportDiagnostic(Diagnostic.Create(diag.Descriptor, Location.None, diag.Args));
            }

            if (result.Model is not null)
            {
                string source = EmitSource(result.Model);
                spc.AddSource($"{result.Model.HintName}.Union.g.cs", SourceText.From(source, Encoding.UTF8));
            }
        });
    }

    private static UnionGenerationResult Transform(GeneratorAttributeSyntaxContext ctx)
    {
        var diagnostics = new List<DiagnosticInfo>();
        if (ctx.TargetSymbol is not INamedTypeSymbol typeSymbol ||
            ctx.TargetNode is not TypeDeclarationSyntax typeDecl)
        {
            return new UnionGenerationResult(null, ImmutableArray<DiagnosticInfo>.Empty);
        }

        if (!typeDecl.Modifiers.Any(SyntaxKind.PartialKeyword))
        {
            diagnostics.Add(new DiagnosticInfo(InvalidUnionDiagnostic, new object[] { typeSymbol.Name, "Type marked with [BinaryUnion] must be declared partial" }));
            return new UnionGenerationResult(null, diagnostics.ToImmutableArray());
        }

        if (!typeSymbol.IsValueType)
        {
            diagnostics.Add(new DiagnosticInfo(InvalidUnionDiagnostic, new object[] { typeSymbol.Name, "[BinaryUnion] can only be applied to a partial struct" }));
            return new UnionGenerationResult(null, diagnostics.ToImmutableArray());
        }

        var containers = new List<ContainerInfo>();
        INamedTypeSymbol? parent = typeSymbol.ContainingType;
        while (parent is not null)
        {
            bool parentPartial = parent.DeclaringSyntaxReferences
                .Select(r => r.GetSyntax())
                .OfType<TypeDeclarationSyntax>()
                .Any(s => s.Modifiers.Any(SyntaxKind.PartialKeyword));

            if (!parentPartial)
            {
                diagnostics.Add(new DiagnosticInfo(InvalidUnionDiagnostic, new object[] { parent.Name, "Containing type of [BinaryUnion] must be declared partial" }));
                return new UnionGenerationResult(null, diagnostics.ToImmutableArray());
            }

            containers.Add(new ContainerInfo(GetTypeDeclarationHeader(parent)));
            parent = parent.ContainingType;
        }
        containers.Reverse();

        int defaultEndianness = 0; // Big
        int tagWidth = 0; // U8
        foreach (AttributeData attr in ctx.Attributes)
        {
            if (attr.AttributeClass?.ToDisplayString() == BinaryUnionAttr)
            {
                if (attr.ConstructorArguments.Length >= 1 && attr.ConstructorArguments[0].Value is byte endianVal)
                {
                    defaultEndianness = endianVal;
                }
                if (attr.ConstructorArguments.Length >= 2 && attr.ConstructorArguments[1].Value is byte widthVal)
                {
                    tagWidth = widthVal;
                }
            }
        }

        byte[]? typeMagic = null;
        var cases = new List<UnionCaseModel>();
        var seenTags = new HashSet<uint>();
        var seenTypes = new HashSet<string>(StringComparer.Ordinal);

        ulong maxTag = tagWidth switch
        {
            0 => byte.MaxValue,
            1 => ushort.MaxValue,
            2 => uint.MaxValue,
            _ => byte.MaxValue,
        };

        foreach (AttributeData attr in typeSymbol.GetAttributes())
        {
            string? attrName = attr.AttributeClass?.ToDisplayString();
            if (attrName == MagicAttr)
            {
                typeMagic = ExtractMagicBytes(attr);
            }
            else if (attrName == UnionCaseAttr && attr.ConstructorArguments.Length == 2)
            {
                if (attr.ConstructorArguments[0].Value is not uint tagValue)
                {
                    diagnostics.Add(new DiagnosticInfo(InvalidUnionDiagnostic, new object[] { typeSymbol.Name, "[UnionCase] tag must be a uint constant" }));
                    continue;
                }

                if (tagValue > maxTag)
                {
                    diagnostics.Add(new DiagnosticInfo(InvalidUnionDiagnostic, new object[] { typeSymbol.Name, $"[UnionCase] tag {tagValue} exceeds configured TagWidth" }));
                    continue;
                }

                if (!seenTags.Add(tagValue))
                {
                    diagnostics.Add(new DiagnosticInfo(InvalidUnionDiagnostic, new object[] { typeSymbol.Name, $"Duplicate [UnionCase] tag {tagValue}" }));
                    continue;
                }

                if (attr.ConstructorArguments[1].Value is not ITypeSymbol payloadType)
                {
                    diagnostics.Add(new DiagnosticInfo(InvalidUnionDiagnostic, new object[] { typeSymbol.Name, "[UnionCase] payload type must be a valid typeof(...) expression" }));
                    continue;
                }

                string fqPayload = payloadType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                if (!seenTypes.Add(fqPayload))
                {
                    diagnostics.Add(new DiagnosticInfo(InvalidUnionDiagnostic, new object[] { typeSymbol.Name, $"Duplicate [UnionCase] payload type '{fqPayload}'" }));
                    continue;
                }

                CaseKind kind = ClassifyPayload(payloadType);
                if (kind == CaseKind.Invalid)
                {
                    diagnostics.Add(new DiagnosticInfo(UnsupportedUnionPayloadDiagnostic, new object[] { typeSymbol.Name, fqPayload }));
                    continue;
                }

                cases.Add(new UnionCaseModel(tagValue, fqPayload, kind));
            }
        }

        if (cases.Count == 0 && diagnostics.Count == 0)
        {
            diagnostics.Add(new DiagnosticInfo(InvalidUnionDiagnostic, new object[] { typeSymbol.Name, "[BinaryUnion] requires at least one [UnionCase] attribute" }));
        }

        if (cases.Count > 255)
        {
            diagnostics.Add(new DiagnosticInfo(InvalidUnionDiagnostic, new object[] { typeSymbol.Name, "[BinaryUnion] supports at most 255 cases" }));
        }

        if (diagnostics.Count > 0)
        {
            return new UnionGenerationResult(null, diagnostics.ToImmutableArray());
        }

        string ns = typeSymbol.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : typeSymbol.ContainingNamespace.ToDisplayString();

        string fqName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        string hintName = typeSymbol.ToDisplayString()
            .Replace('<', '_')
            .Replace('>', '_')
            .Replace('.', '_')
            .Replace(' ', '_');

        var model = new UnionModel(
            Namespace: ns,
            HintName: hintName,
            TypeName: typeSymbol.Name,
            FullyQualifiedName: fqName,
            DeclarationHeader: GetTypeDeclarationHeader(typeSymbol),
            Containers: containers.ToImmutableArray(),
            DefaultEndianness: defaultEndianness,
            TagWidth: tagWidth,
            TypeMagic: typeMagic,
            Cases: cases.ToImmutableArray());

        return new UnionGenerationResult(model, ImmutableArray<DiagnosticInfo>.Empty);
    }

    private static CaseKind ClassifyPayload(ITypeSymbol type)
    {
        if (type.GetAttributes().Any(a =>
            a.AttributeClass?.ToDisplayString() == BinaryStructAttr ||
            a.AttributeClass?.ToDisplayString() == BinaryUnionAttr))
        {
            return CaseKind.NestedBinary;
        }

        return type.SpecialType switch
        {
            SpecialType.System_Boolean => CaseKind.BoolByte,
            SpecialType.System_Byte => CaseKind.U8,
            SpecialType.System_SByte => CaseKind.I8,
            SpecialType.System_UInt16 => CaseKind.U16,
            SpecialType.System_Int16 => CaseKind.I16,
            SpecialType.System_UInt32 => CaseKind.U32,
            SpecialType.System_Int32 => CaseKind.I32,
            SpecialType.System_UInt64 => CaseKind.U64,
            SpecialType.System_Int64 => CaseKind.I64,
            SpecialType.System_Single => CaseKind.F32,
            SpecialType.System_Double => CaseKind.F64,
            _ => CaseKind.Invalid,
        };
    }

    private static string GetTypeDeclarationHeader(INamedTypeSymbol typeSymbol)
    {
        var sb = new StringBuilder();
        if (typeSymbol.IsValueType && typeSymbol.IsReadOnly)
        {
            sb.Append("readonly ");
        }
        sb.Append("partial ");
        if (typeSymbol.IsRecord)
        {
            sb.Append("record ");
            sb.Append(typeSymbol.IsValueType ? "struct " : "class ");
        }
        else
        {
            sb.Append(typeSymbol.IsValueType ? "struct " : "class ");
        }
        sb.Append(typeSymbol.Name);
        return sb.ToString();
    }

    private static byte[]? ExtractMagicBytes(AttributeData attr)
    {
        if (attr.ConstructorArguments.Length != 1) return null;
        TypedConstant arg = attr.ConstructorArguments[0];
        if (arg.Kind != TypedConstantKind.Array || arg.Values.IsDefaultOrEmpty) return null;

        byte[] bytes = new byte[arg.Values.Length];
        for (int i = 0; i < arg.Values.Length; i++)
        {
            if (arg.Values[i].Value is byte b)
            {
                bytes[i] = b;
            }
        }
        return bytes;
    }

    private static string EmitSource(UnionModel model)
    {
        var sb = new StringBuilder(3072);
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(model.Namespace))
        {
            sb.Append("namespace ").Append(model.Namespace).AppendLine(";");
            sb.AppendLine();
        }

        string indent = string.Empty;
        foreach (ContainerInfo c in model.Containers)
        {
            sb.Append(indent).AppendLine(c.DeclarationHeader);
            sb.Append(indent).AppendLine("{");
            indent += "    ";
        }

        sb.Append(indent).AppendLine("[global::System.Runtime.CompilerServices.UnionAttribute]");
        sb.Append(indent).Append(model.DeclarationHeader).AppendLine(" : global::System.Runtime.CompilerServices.IUnion");
        sb.Append(indent).AppendLine("{");
        string ind = indent + "    ";

        EmitUnionCore(sb, model, ind);
        sb.AppendLine();
        EmitParseMethod(sb, model, ind);
        sb.AppendLine();
        EmitTryParseMethod(sb, model, ind);
        sb.AppendLine();
        EmitWriteMethods(sb, model, ind);
        sb.AppendLine();
        EmitParserStructs(sb, model, ind);

        sb.Append(indent).AppendLine("}");

        for (int i = model.Containers.Length - 1; i >= 0; i--)
        {
            indent = indent.Substring(0, indent.Length - 4);
            sb.Append(indent).AppendLine("}");
        }

        return sb.ToString();
    }

    private static void EmitUnionCore(StringBuilder sb, UnionModel model, string ind)
    {
        sb.Append(ind).AppendLine("private readonly byte _tag;");
        for (int i = 0; i < model.Cases.Length; i++)
        {
            sb.Append(ind).Append("private readonly ").Append(model.Cases[i].PayloadTypeName).Append(" _case_").Append(i).AppendLine(";");
        }
        sb.AppendLine();

        // Constructors (1 per case — enables C# 15 implicit conversion from each case type!)
        for (int i = 0; i < model.Cases.Length; i++)
        {
            UnionCaseModel c = model.Cases[i];
            sb.Append(ind).AppendLine("[MethodImpl(MethodImplOptions.AggressiveInlining)]");
            sb.Append(ind).Append("public ").Append(model.TypeName).Append('(').Append(c.PayloadTypeName).AppendLine(" value)");
            sb.Append(ind).AppendLine("{");
            sb.Append(ind).Append("    _tag = ").Append(i + 1).AppendLine(";");
            for (int j = 0; j < model.Cases.Length; j++)
            {
                sb.Append(ind).Append("    _case_").Append(j).Append(j == i ? " = value;" : " = default!;").AppendLine();
            }
            sb.Append(ind).AppendLine("}");
            sb.AppendLine();
        }

        sb.Append(ind).AppendLine("public bool HasValue");
        sb.Append(ind).AppendLine("{");
        sb.Append(ind).AppendLine("    [MethodImpl(MethodImplOptions.AggressiveInlining)]");
        sb.Append(ind).AppendLine("    get => _tag != 0;");
        sb.Append(ind).AppendLine("}");
        sb.AppendLine();

        sb.Append(ind).AppendLine("public object? Value => _tag switch");
        sb.Append(ind).AppendLine("{");
        for (int i = 0; i < model.Cases.Length; i++)
        {
            sb.Append(ind).Append("    ").Append(i + 1).Append(" => _case_").Append(i).AppendLine(",");
        }
        sb.Append(ind).AppendLine("    _ => null,");
        sb.Append(ind).AppendLine("};");

        for (int i = 0; i < model.Cases.Length; i++)
        {
            UnionCaseModel c = model.Cases[i];
            sb.AppendLine();
            sb.Append(ind).AppendLine("[MethodImpl(MethodImplOptions.AggressiveInlining)]");
            sb.Append(ind).Append("public bool TryGetValue(out ").Append(c.PayloadTypeName).AppendLine(" value)");
            sb.Append(ind).AppendLine("{");
            sb.Append(ind).Append("    if (_tag == ").Append(i + 1).AppendLine(")");
            sb.Append(ind).AppendLine("    {");
            sb.Append(ind).Append("        value = _case_").Append(i).AppendLine(";");
            sb.Append(ind).AppendLine("        return true;");
            sb.Append(ind).AppendLine("    }");
            sb.Append(ind).AppendLine("    value = default!;");
            sb.Append(ind).AppendLine("    return false;");
            sb.Append(ind).AppendLine("}");
        }
    }

    private static void EmitParseMethod(StringBuilder sb, UnionModel model, string ind)
    {
        string fq = model.FullyQualifiedName;
        string suffix = model.DefaultEndianness == 0 ? "BE" : "LE";
        string inner = ind + "    ";

        sb.Append(ind).AppendLine("[MethodImpl(MethodImplOptions.AggressiveInlining)]");
        sb.Append(ind).Append("public static global::SpanForge.ParseResult<").Append(fq).AppendLine("> Parse(ref global::SpanForge.ByteCursor cursor)");
        sb.Append(ind).AppendLine("{");
        sb.Append(inner).AppendLine("var __cp = cursor.Save();");

        if (model.TypeMagic is { Length: > 0 } tm)
        {
            if (tm.Length == 1)
            {
                sb.Append(inner).Append("var __tm = cursor.TagByte(").Append(tm[0]).AppendLine(");");
            }
            else
            {
                sb.Append(inner).Append("var __tm = cursor.Tag([");
                for (int k = 0; k < tm.Length; k++)
                {
                    if (k > 0) sb.Append(", ");
                    sb.Append("0x").Append(tm[k].ToString("X2"));
                }
                sb.AppendLine("]);");
            }
            sb.Append(inner).Append("if (!__tm.IsParsed) { cursor.Reset(__cp); return __tm.PropagateNonParsed<").Append(fq).AppendLine(">(); }");
        }

        string tagReadExpr = model.TagWidth switch
        {
            0 => "cursor.ReadU8()",
            1 => $"cursor.ReadU16{suffix}()",
            2 => $"cursor.ReadU32{suffix}()",
            _ => "cursor.ReadU8()",
        };
        string tagType = model.TagWidth switch
        {
            0 => "byte",
            1 => "ushort",
            2 => "uint",
            _ => "byte",
        };

        sb.Append(inner).Append("var __tagRes = ").Append(tagReadExpr).AppendLine(";");
        sb.Append(inner).Append("if (!__tagRes.TryGetValue(out global::SpanForge.Parsed<").Append(tagType).Append("> __tagParsed)) { cursor.Reset(__cp); return __tagRes.PropagateNonParsed<").Append(fq).AppendLine(">(); }");
        sb.Append(inner).AppendLine("uint __wireTag = __tagParsed.Value;");
        sb.Append(inner).AppendLine("switch (__wireTag)");
        sb.Append(inner).AppendLine("{");

        string caseInd = inner + "    ";
        string caseBodyInd = caseInd + "    ";

        for (int i = 0; i < model.Cases.Length; i++)
        {
            UnionCaseModel c = model.Cases[i];
            sb.Append(caseInd).Append("case ").Append(c.Tag).AppendLine("u:");
            sb.Append(caseInd).AppendLine("{");

            if (c.Kind == CaseKind.BoolByte)
            {
                sb.Append(caseBodyInd).AppendLine("var __r = cursor.ReadU8();");
                sb.Append(caseBodyInd).Append("if (!__r.TryGetValue(out global::SpanForge.Parsed<byte> __p)) { cursor.Reset(__cp); return __r.PropagateNonParsed<").Append(fq).AppendLine(">(); }");
                sb.Append(caseBodyInd).Append("return new global::SpanForge.Parsed<").Append(fq).Append(">(new ").Append(fq).AppendLine("(__p.Value != 0));");
            }
            else
            {
                string readExpr = c.Kind switch
                {
                    CaseKind.U8 => "cursor.ReadU8()",
                    CaseKind.I8 => "cursor.ReadI8()",
                    CaseKind.U16 => $"cursor.ReadU16{suffix}()",
                    CaseKind.I16 => $"cursor.ReadI16{suffix}()",
                    CaseKind.U32 => $"cursor.ReadU32{suffix}()",
                    CaseKind.I32 => $"cursor.ReadI32{suffix}()",
                    CaseKind.U64 => $"cursor.ReadU64{suffix}()",
                    CaseKind.I64 => $"cursor.ReadI64{suffix}()",
                    CaseKind.F32 => $"cursor.ReadF32{suffix}()",
                    CaseKind.F64 => $"cursor.ReadF64{suffix}()",
                    CaseKind.NestedBinary => $"{c.PayloadTypeName}.Parse(ref cursor)",
                    _ => throw new InvalidOperationException(),
                };
                sb.Append(caseBodyInd).Append("var __r = ").Append(readExpr).AppendLine(";");
                sb.Append(caseBodyInd).Append("if (!__r.TryGetValue(out global::SpanForge.Parsed<").Append(c.PayloadTypeName).Append("> __p)) { cursor.Reset(__cp); return __r.PropagateNonParsed<").Append(fq).AppendLine(">(); }");
                sb.Append(caseBodyInd).Append("return new global::SpanForge.Parsed<").Append(fq).Append(">(new ").Append(fq).AppendLine("(__p.Value));");
            }

            sb.Append(caseInd).AppendLine("}");
        }

        sb.Append(caseInd).AppendLine("default:");
        sb.Append(caseBodyInd).AppendLine("cursor.Reset(__cp);");
        sb.Append(caseBodyInd).AppendLine("return global::SpanForge.Backtrack.At(__cp.Position, global::SpanForge.ErrorKind.Alt, \"Unknown [BinaryUnion] tag\");");
        sb.Append(inner).AppendLine("}");
        sb.Append(ind).AppendLine("}");
    }

    private static void EmitTryParseMethod(StringBuilder sb, UnionModel model, string ind)
    {
        string fq = model.FullyQualifiedName;
        string suffix = model.DefaultEndianness == 0 ? "BE" : "LE";
        string inner = ind + "    ";

        sb.Append(ind).AppendLine("[MethodImpl(MethodImplOptions.AggressiveInlining)]");
        sb.Append(ind).Append("public static global::SpanForge.FastStatus TryParse(ref global::SpanForge.ByteCursor cursor, out ").Append(fq).AppendLine(" value)");
        sb.Append(ind).AppendLine("{");
        sb.Append(inner).AppendLine("int __start = cursor.Position;");

        if (model.TypeMagic is { Length: > 0 } tm)
        {
            if (tm.Length == 1)
            {
                sb.Append(inner).Append("var __tm = cursor.TryTagByte(").Append(tm[0]).AppendLine(", out _);");
            }
            else
            {
                sb.Append(inner).Append("var __tm = cursor.TryTag([");
                for (int k = 0; k < tm.Length; k++)
                {
                    if (k > 0) sb.Append(", ");
                    sb.Append("0x").Append(tm[k].ToString("X2"));
                }
                sb.AppendLine("]);");
            }
            sb.Append(inner).AppendLine("if (!__tm.IsParsed) { Unsafe.SkipInit(out value); return __tm; }");
        }

        string tagCall = model.TagWidth switch
        {
            0 => "cursor.TryReadU8(out byte __tagRaw)",
            1 => $"cursor.TryReadU16{suffix}(out ushort __tagRaw)",
            2 => $"cursor.TryReadU32{suffix}(out uint __tagRaw)",
            _ => "cursor.TryReadU8(out byte __tagRaw)",
        };

        sb.Append(inner).Append("var __tagSt = ").Append(tagCall).AppendLine(";");
        sb.Append(inner).AppendLine("if (!__tagSt.IsParsed) { cursor.Position = __start; Unsafe.SkipInit(out value); return __tagSt; }");
        sb.Append(inner).AppendLine("uint __wireTag = __tagRaw;");
        sb.Append(inner).AppendLine("switch (__wireTag)");
        sb.Append(inner).AppendLine("{");

        string caseInd = inner + "    ";
        string caseBodyInd = caseInd + "    ";

        for (int i = 0; i < model.Cases.Length; i++)
        {
            UnionCaseModel c = model.Cases[i];
            sb.Append(caseInd).Append("case ").Append(c.Tag).AppendLine("u:");
            sb.Append(caseInd).AppendLine("{");

            string call = c.Kind switch
            {
                CaseKind.BoolByte => "cursor.TryReadBool(out bool __p)",
                CaseKind.U8 => "cursor.TryReadU8(out byte __p)",
                CaseKind.I8 => "cursor.TryReadI8(out sbyte __p)",
                CaseKind.U16 => $"cursor.TryReadU16{suffix}(out ushort __p)",
                CaseKind.I16 => $"cursor.TryReadI16{suffix}(out short __p)",
                CaseKind.U32 => $"cursor.TryReadU32{suffix}(out uint __p)",
                CaseKind.I32 => $"cursor.TryReadI32{suffix}(out int __p)",
                CaseKind.U64 => $"cursor.TryReadU64{suffix}(out ulong __p)",
                CaseKind.I64 => $"cursor.TryReadI64{suffix}(out long __p)",
                CaseKind.F32 => $"cursor.TryReadF32{suffix}(out float __p)",
                CaseKind.F64 => $"cursor.TryReadF64{suffix}(out double __p)",
                CaseKind.NestedBinary => $"{c.PayloadTypeName}.TryParse(ref cursor, out {c.PayloadTypeName} __p)",
                _ => throw new InvalidOperationException(),
            };

            sb.Append(caseBodyInd).Append("var __s = ").Append(call).AppendLine(";");
            sb.Append(caseBodyInd).AppendLine("if (!__s.IsParsed) { cursor.Position = __start; Unsafe.SkipInit(out value); return __s; }");
            sb.Append(caseBodyInd).Append("value = new ").Append(fq).AppendLine("(__p);");
            sb.Append(caseBodyInd).AppendLine("return global::SpanForge.FastStatus.Parsed;");
            sb.Append(caseInd).AppendLine("}");
        }

        sb.Append(caseInd).AppendLine("default:");
        sb.Append(caseBodyInd).AppendLine("cursor.Position = __start;");
        sb.Append(caseBodyInd).AppendLine("Unsafe.SkipInit(out value);");
        sb.Append(caseBodyInd).AppendLine("return global::SpanForge.FastStatus.Backtrack;");
        sb.Append(inner).AppendLine("}");
        sb.Append(ind).AppendLine("}");
    }

    private static void EmitWriteMethods(StringBuilder sb, UnionModel model, string ind)
    {
        string inner = ind + "    ";
        string suffix = model.DefaultEndianness == 0 ? "BE" : "LE";
        string defaultEndianEnum = model.DefaultEndianness == 0 ? "Big" : "Little";

        sb.Append(ind).AppendLine("[MethodImpl(MethodImplOptions.AggressiveInlining)]");
        sb.Append(ind).AppendLine("public bool TryWrite(ref global::SpanForge.ByteWriter writer, global::System.ReadOnlySpan<byte> sourceBuffer = default)");
        sb.Append(ind).AppendLine("{");
        sb.Append(inner).AppendLine("var __cp = writer.Save();");

        if (model.TypeMagic is { Length: > 0 } tm)
        {
            if (tm.Length == 1)
            {
                sb.Append(inner).Append("if (!writer.TryWriteU8(").Append(tm[0]).AppendLine(")) { writer.Reset(__cp); return false; }");
            }
            else
            {
                sb.Append(inner).Append("if (!writer.TryWriteBytes([");
                for (int k = 0; k < tm.Length; k++)
                {
                    if (k > 0) sb.Append(", ");
                    sb.Append("0x").Append(tm[k].ToString("X2"));
                }
                sb.AppendLine("])) { writer.Reset(__cp); return false; }");
            }
        }

        sb.Append(inner).AppendLine("switch (_tag)");
        sb.Append(inner).AppendLine("{");

        string caseInd = inner + "    ";
        string bodyInd = caseInd + "    ";

        for (int i = 0; i < model.Cases.Length; i++)
        {
            UnionCaseModel c = model.Cases[i];
            sb.Append(caseInd).Append("case ").Append(i + 1).AppendLine(":");
            string writeTagCall = model.TagWidth switch
            {
                0 => $"writer.TryWriteU8({c.Tag})",
                1 => $"writer.TryWriteU16{suffix}({c.Tag})",
                2 => $"writer.TryWriteU32{suffix}({c.Tag}u)",
                _ => $"writer.TryWriteU8({c.Tag})",
            };
            string writePayloadCall = c.Kind switch
            {
                CaseKind.BoolByte => $"writer.TryWriteBool(_case_{i})",
                CaseKind.U8 => $"writer.TryWriteU8(_case_{i})",
                CaseKind.I8 => $"writer.TryWriteI8(_case_{i})",
                CaseKind.U16 => $"writer.TryWriteU16{suffix}(_case_{i})",
                CaseKind.I16 => $"writer.TryWriteI16{suffix}(_case_{i})",
                CaseKind.U32 => $"writer.TryWriteU32{suffix}(_case_{i})",
                CaseKind.I32 => $"writer.TryWriteI32{suffix}(_case_{i})",
                CaseKind.U64 => $"writer.TryWriteU64{suffix}(_case_{i})",
                CaseKind.I64 => $"writer.TryWriteI64{suffix}(_case_{i})",
                CaseKind.F32 => $"writer.TryWriteF32{suffix}(_case_{i})",
                CaseKind.F64 => $"writer.TryWriteF64{suffix}(_case_{i})",
                CaseKind.NestedBinary => $"_case_{i}.TryWrite(ref writer, sourceBuffer)",
                _ => throw new InvalidOperationException(),
            };
            sb.Append(bodyInd).Append("if (!").Append(writeTagCall).Append(" || !").Append(writePayloadCall).AppendLine(") { writer.Reset(__cp); return false; }");
            sb.Append(bodyInd).AppendLine("return true;");
        }

        sb.Append(caseInd).AppendLine("default:");
        sb.Append(bodyInd).AppendLine("writer.Reset(__cp);");
        sb.Append(bodyInd).AppendLine("return false;");
        sb.Append(inner).AppendLine("}");
        sb.Append(ind).AppendLine("}");
        sb.AppendLine();

        sb.Append(ind).AppendLine("[MethodImpl(MethodImplOptions.AggressiveInlining)]");
        sb.Append(ind).AppendLine("public int TryWriteTo(global::System.Span<byte> destination, global::System.ReadOnlySpan<byte> sourceBuffer = default)");
        sb.Append(ind).AppendLine("{");
        sb.Append(inner).Append("var __writer = new global::SpanForge.ByteWriter(destination, global::SpanForge.Endianness.").Append(defaultEndianEnum).AppendLine(");");
        sb.Append(inner).AppendLine("return TryWrite(ref __writer, sourceBuffer) ? __writer.Position : -1;");
        sb.Append(ind).AppendLine("}");
        sb.AppendLine();

        sb.Append(ind).AppendLine("public void WriteTo(global::System.Buffers.IBufferWriter<byte> output, global::System.ReadOnlySpan<byte> sourceBuffer = default, int initialHint = 256)");
        sb.Append(ind).AppendLine("{");
        sb.Append(inner).AppendLine("int __hint = initialHint > 0 ? initialHint : 64;");
        sb.Append(inner).AppendLine("while (true)");
        sb.Append(inner).AppendLine("{");
        sb.Append(inner).AppendLine("    var __span = output.GetSpan(__hint);");
        sb.Append(inner).Append("    var __writer = new global::SpanForge.ByteWriter(__span, global::SpanForge.Endianness.").Append(defaultEndianEnum).AppendLine(");");
        sb.Append(inner).AppendLine("    if (TryWrite(ref __writer, sourceBuffer))");
        sb.Append(inner).AppendLine("    {");
        sb.Append(inner).AppendLine("        output.Advance(__writer.Position);");
        sb.Append(inner).AppendLine("        return;");
        sb.Append(inner).AppendLine("    }");
        sb.Append(inner).AppendLine("    int __next = __span.Length >= __hint ? __span.Length : __hint;");
        sb.Append(inner).AppendLine("    if (__next >= 67_108_864)");
        sb.Append(inner).AppendLine("    {");
        sb.Append(inner).AppendLine("        throw new global::System.InvalidOperationException(\"Failed to write binary union to IBufferWriter<byte>.\");");
        sb.Append(inner).AppendLine("    }");
        sb.Append(inner).AppendLine("    __hint = __next * 2;");
        sb.Append(inner).AppendLine("}");
        sb.Append(ind).AppendLine("}");
    }

    private static void EmitParserStructs(StringBuilder sb, UnionModel model, string ind)
    {
        string fq = model.FullyQualifiedName;
        sb.Append(ind).Append("public readonly struct Parser : global::SpanForge.IParser<").Append(fq).AppendLine(">");
        sb.Append(ind).AppendLine("{");
        sb.Append(ind).AppendLine("    [MethodImpl(MethodImplOptions.AggressiveInlining)]");
        sb.Append(ind).Append("    public global::SpanForge.ParseResult<").Append(fq).AppendLine("> Parse(ref global::SpanForge.ByteCursor cursor) =>");
        sb.Append(ind).Append("        ").Append(fq).AppendLine(".Parse(ref cursor);");
        sb.Append(ind).AppendLine("}");
        sb.AppendLine();
        sb.Append(ind).Append("public readonly struct TryParser : global::SpanForge.ITryParser<").Append(fq).AppendLine(">");
        sb.Append(ind).AppendLine("{");
        sb.Append(ind).AppendLine("    [MethodImpl(MethodImplOptions.AggressiveInlining)]");
        sb.Append(ind).Append("    public global::SpanForge.FastStatus TryParse(ref global::SpanForge.ByteCursor cursor, out ").Append(fq).AppendLine(" value) =>");
        sb.Append(ind).Append("        ").Append(fq).AppendLine(".TryParse(ref cursor, out value);");
        sb.Append(ind).AppendLine("}");
    }

    private enum CaseKind
    {
        Invalid = 0,
        BoolByte,
        U8,
        I8,
        U16,
        I16,
        U32,
        I32,
        U64,
        I64,
        F32,
        F64,
        NestedBinary,
    }

    private sealed class UnionGenerationResult
    {
        public UnionGenerationResult(UnionModel? model, ImmutableArray<DiagnosticInfo> diagnostics)
        {
            Model = model;
            Diagnostics = diagnostics;
        }

        public UnionModel? Model { get; }
        public ImmutableArray<DiagnosticInfo> Diagnostics { get; }
    }

    private sealed class DiagnosticInfo
    {
        public DiagnosticInfo(DiagnosticDescriptor descriptor, object[] args)
        {
            Descriptor = descriptor;
            Args = args;
        }

        public DiagnosticDescriptor Descriptor { get; }
        public object[] Args { get; }
    }

    private sealed class ContainerInfo
    {
        public ContainerInfo(string declarationHeader) => DeclarationHeader = declarationHeader;
        public string DeclarationHeader { get; }
    }

    private sealed class UnionModel
    {
        public UnionModel(
            string Namespace,
            string HintName,
            string TypeName,
            string FullyQualifiedName,
            string DeclarationHeader,
            ImmutableArray<ContainerInfo> Containers,
            int DefaultEndianness,
            int TagWidth,
            byte[]? TypeMagic,
            ImmutableArray<UnionCaseModel> Cases)
        {
            this.Namespace = Namespace;
            this.HintName = HintName;
            this.TypeName = TypeName;
            this.FullyQualifiedName = FullyQualifiedName;
            this.DeclarationHeader = DeclarationHeader;
            this.Containers = Containers;
            this.DefaultEndianness = DefaultEndianness;
            this.TagWidth = TagWidth;
            this.TypeMagic = TypeMagic;
            this.Cases = Cases;
        }

        public string Namespace { get; }
        public string HintName { get; }
        public string TypeName { get; }
        public string FullyQualifiedName { get; }
        public string DeclarationHeader { get; }
        public ImmutableArray<ContainerInfo> Containers { get; }
        public int DefaultEndianness { get; }
        public int TagWidth { get; }
        public byte[]? TypeMagic { get; }
        public ImmutableArray<UnionCaseModel> Cases { get; }
    }

    private sealed class UnionCaseModel
    {
        public UnionCaseModel(uint tag, string payloadTypeName, CaseKind kind)
        {
            Tag = tag;
            PayloadTypeName = payloadTypeName;
            Kind = kind;
        }

        public uint Tag { get; }
        public string PayloadTypeName { get; }
        public CaseKind Kind { get; }
    }
}
