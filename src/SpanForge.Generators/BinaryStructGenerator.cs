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
public sealed class BinaryStructGenerator : IIncrementalGenerator
{
    private const string BinaryStructAttr = "SpanForge.BinaryStructAttribute";
    private const string BinaryUnionAttr = "SpanForge.BinaryUnionAttribute";
    private const string MagicAttr = "SpanForge.MagicAttribute";
    private const string EndianAttr = "SpanForge.EndianAttribute";
    private const string VarIntAttr = "SpanForge.VarIntAttribute";
    private const string LengthPrefixedAttr = "SpanForge.LengthPrefixedAttribute";
    private const string FixedBytesAttr = "SpanForge.FixedBytesAttribute";
    private const string AlignAttr = "SpanForge.AlignAttribute";
    private const string PadAttr = "SpanForge.PadAttribute";
    private const string BitsAttr = "SpanForge.BitsAttribute";
    private const string AssertValueAttr = "SpanForge.AssertValueAttribute";

    private static readonly DiagnosticDescriptor NonPartialDiagnostic = new(
        id: "SFG001",
        title: "BinaryStruct type must be partial",
        messageFormat: "Type '{0}' is marked with [BinaryStruct] and must be declared partial",
        category: "SpanForge.Generators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedMemberDiagnostic = new(
        id: "SFG002",
        title: "Unsupported BinaryStruct member type",
        messageFormat: "Member '{0}' of type '{1}' is not supported by [BinaryStruct]; use a primitive numeric type, bool, ByteSlice (with [FixedBytes] or [LengthPrefixed]), or another [BinaryStruct] type",
        category: "SpanForge.Generators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidAttributeDiagnostic = new(
        id: "SFG003",
        title: "Invalid BinaryStruct attribute configuration",
        messageFormat: "Invalid attribute configuration on '{0}': {1}",
        category: "SpanForge.Generators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<GenerationResult> pipeline = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                fullyQualifiedMetadataName: BinaryStructAttr,
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
                spc.AddSource($"{result.Model.HintName}.g.cs", SourceText.From(source, Encoding.UTF8));
            }
        });
    }

    private static GenerationResult Transform(GeneratorAttributeSyntaxContext ctx)
    {
        var diagnostics = new List<DiagnosticInfo>();
        if (ctx.TargetSymbol is not INamedTypeSymbol typeSymbol ||
            ctx.TargetNode is not TypeDeclarationSyntax typeDecl)
        {
            return new GenerationResult(null, ImmutableArray<DiagnosticInfo>.Empty);
        }

        if (!typeDecl.Modifiers.Any(SyntaxKind.PartialKeyword))
        {
            diagnostics.Add(new DiagnosticInfo(NonPartialDiagnostic, new object[] { typeSymbol.Name }));
            return new GenerationResult(null, diagnostics.ToImmutableArray());
        }

        // Check enclosing types are partial too
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
                diagnostics.Add(new DiagnosticInfo(NonPartialDiagnostic, new object[] { parent.Name }));
                return new GenerationResult(null, diagnostics.ToImmutableArray());
            }

            containers.Add(new ContainerInfo(GetTypeDeclarationHeader(parent)));
            parent = parent.ContainingType;
        }
        containers.Reverse();

        int defaultEndianness = 0; // 0 = Big, 1 = Little
        foreach (AttributeData attr in ctx.Attributes)
        {
            if (attr.AttributeClass?.ToDisplayString() == BinaryStructAttr &&
                attr.ConstructorArguments.Length == 1 &&
                attr.ConstructorArguments[0].Value is byte endianVal)
            {
                defaultEndianness = endianVal;
            }
        }

        byte[]? typeMagic = ExtractMagic(typeSymbol.GetAttributes());

        // Check if this is a positional record (primary constructor parameters matching properties)
        IMethodSymbol? primaryCtor = FindPositionalRecordConstructor(typeSymbol);
        var members = new List<MemberModel>();

        if (primaryCtor is not null)
        {
            foreach (IParameterSymbol param in primaryCtor.Parameters)
            {
                IPropertySymbol? matchingProp = typeSymbol.GetMembers(param.Name)
                    .OfType<IPropertySymbol>()
                    .FirstOrDefault();

                var allAttrs = new List<AttributeData>();
                allAttrs.AddRange(param.GetAttributes());
                if (matchingProp is not null)
                {
                    allAttrs.AddRange(matchingProp.GetAttributes());
                }

                MemberModel? member = BuildMemberModel(
                    param.Name,
                    param.Type,
                    allAttrs,
                    defaultEndianness,
                    diagnostics);

                if (member is not null)
                {
                    members.Add(member);
                }
            }
        }
        else
        {
            foreach (ISymbol sym in typeSymbol.GetMembers())
            {
                if (sym.IsStatic || sym.IsImplicitlyDeclared) continue;

                if (sym is IPropertySymbol prop)
                {
                    if (prop.IsIndexer || prop.SetMethod is null) continue;
                    MemberModel? member = BuildMemberModel(
                        prop.Name,
                        prop.Type,
                        prop.GetAttributes(),
                        defaultEndianness,
                        diagnostics);
                    if (member is not null) members.Add(member);
                }
                else if (sym is IFieldSymbol field)
                {
                    if (field.IsConst || field.IsReadOnly) continue;
                    MemberModel? member = BuildMemberModel(
                        field.Name,
                        field.Type,
                        field.GetAttributes(),
                        defaultEndianness,
                        diagnostics);
                    if (member is not null) members.Add(member);
                }
            }
        }

        if (diagnostics.Count > 0)
        {
            return new GenerationResult(null, diagnostics.ToImmutableArray());
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

        var model = new TypeModel(
            Namespace: ns,
            HintName: hintName,
            TypeName: typeSymbol.Name,
            FullyQualifiedName: fqName,
            DeclarationHeader: GetTypeDeclarationHeader(typeSymbol),
            Containers: containers.ToImmutableArray(),
            DefaultEndianness: defaultEndianness,
            TypeMagic: typeMagic,
            UsePositionalCtor: primaryCtor is not null,
            Members: members.ToImmutableArray());

        return new GenerationResult(model, ImmutableArray<DiagnosticInfo>.Empty);
    }

    private static IMethodSymbol? FindPositionalRecordConstructor(INamedTypeSymbol typeSymbol)
    {
        if (!typeSymbol.IsRecord) return null;

        foreach (IMethodSymbol ctor in typeSymbol.InstanceConstructors)
        {
            if (ctor.IsImplicitlyDeclared || ctor.Parameters.Length == 0) continue;
            // Skip copy constructor (single parameter of same type)
            if (ctor.Parameters.Length == 1 &&
                SymbolEqualityComparer.Default.Equals(ctor.Parameters[0].Type, typeSymbol))
            {
                continue;
            }

            // Primary ctor parameters on records have matching public auto-properties
            bool allMatchProps = ctor.Parameters.All(p =>
                typeSymbol.GetMembers(p.Name).OfType<IPropertySymbol>().Any());
            if (allMatchProps) return ctor;
        }

        return null;
    }

    private static string GetTypeDeclarationHeader(INamedTypeSymbol typeSymbol)
    {
        var sb = new StringBuilder();
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

    private static MemberModel? BuildMemberModel(
        string name,
        ITypeSymbol type,
        IEnumerable<AttributeData> attrs,
        int defaultEndianness,
        List<DiagnosticInfo> diagnostics)
    {
        int endianness = defaultEndianness;
        byte[]? magic = null;
        int? varIntEncoding = null;
        int? lengthPrefix = null;
        int? fixedBytes = null;
        int? align = null;
        int? pad = null;
        int? bits = null;
        ulong? assertValue = null;

        foreach (AttributeData attr in attrs)
        {
            string? attrName = attr.AttributeClass?.ToDisplayString();
            switch (attrName)
            {
                case MagicAttr:
                    magic = ExtractMagicBytes(attr);
                    break;
                case EndianAttr when attr.ConstructorArguments.Length == 1 && attr.ConstructorArguments[0].Value is byte e:
                    endianness = e;
                    break;
                case VarIntAttr:
                    varIntEncoding = attr.ConstructorArguments.Length == 1 && attr.ConstructorArguments[0].Value is byte ve
                        ? (int)ve
                        : 0;
                    break;
                case LengthPrefixedAttr:
                    lengthPrefix = attr.ConstructorArguments.Length == 1 && attr.ConstructorArguments[0].Value is byte lp
                        ? (int)lp
                        : 1; // U16
                    break;
                case FixedBytesAttr when attr.ConstructorArguments.Length == 1 && attr.ConstructorArguments[0].Value is int fb:
                    if (fb < 0)
                    {
                        diagnostics.Add(new DiagnosticInfo(InvalidAttributeDiagnostic, new object[] { name, "[FixedBytes] count must be >= 0" }));
                        return null;
                    }
                    fixedBytes = fb;
                    break;
                case AlignAttr when attr.ConstructorArguments.Length == 1 && attr.ConstructorArguments[0].Value is int al:
                    if (al <= 0 || (al & (al - 1)) != 0)
                    {
                        diagnostics.Add(new DiagnosticInfo(InvalidAttributeDiagnostic, new object[] { name, "[Align] must be a power of two" }));
                        return null;
                    }
                    align = al;
                    break;
                case PadAttr when attr.ConstructorArguments.Length == 1 && attr.ConstructorArguments[0].Value is int pd:
                    if (pd < 0)
                    {
                        diagnostics.Add(new DiagnosticInfo(InvalidAttributeDiagnostic, new object[] { name, "[Pad] bytes must be >= 0" }));
                        return null;
                    }
                    pad = pd;
                    break;
                case BitsAttr when attr.ConstructorArguments.Length == 1 && attr.ConstructorArguments[0].Value is int bt:
                    if (bt <= 0 || bt > 64)
                    {
                        diagnostics.Add(new DiagnosticInfo(InvalidAttributeDiagnostic, new object[] { name, "[Bits] count must be in [1..64]" }));
                        return null;
                    }
                    bits = bt;
                    break;
                case AssertValueAttr when attr.ConstructorArguments.Length == 1 && attr.ConstructorArguments[0].Value is ulong av:
                    assertValue = av;
                    break;
            }
        }

        string fqType = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        MemberKind kind;

        if (bits.HasValue)
        {
            kind = type.SpecialType switch
            {
                SpecialType.System_Boolean => bits.Value == 1 ? MemberKind.BitBool : MemberKind.Invalid,
                SpecialType.System_Byte => bits.Value <= 8 ? MemberKind.BitU8 : MemberKind.Invalid,
                SpecialType.System_UInt16 => bits.Value <= 16 ? MemberKind.BitU16 : MemberKind.Invalid,
                SpecialType.System_UInt32 => bits.Value <= 32 ? MemberKind.BitU32 : MemberKind.Invalid,
                SpecialType.System_Int32 => bits.Value <= 32 ? MemberKind.BitI32 : MemberKind.Invalid,
                SpecialType.System_UInt64 => MemberKind.BitU64,
                _ => MemberKind.Invalid,
            };

            if (kind == MemberKind.Invalid)
            {
                diagnostics.Add(new DiagnosticInfo(InvalidAttributeDiagnostic, new object[] { name, $"[Bits({bits.Value})] is not valid for type '{fqType}'" }));
                return null;
            }
        }
        else if (varIntEncoding.HasValue)
        {
            kind = (varIntEncoding.Value, type.SpecialType) switch
            {
                (0, SpecialType.System_UInt64) => MemberKind.VarUInt64,
                (1, SpecialType.System_Int64) => MemberKind.VarInt64ZigZag,
                (2, SpecialType.System_UInt64) => MemberKind.QuicVarUInt64,
                _ => MemberKind.Invalid,
            };
            if (kind == MemberKind.Invalid)
            {
                diagnostics.Add(new DiagnosticInfo(InvalidAttributeDiagnostic, new object[] { name, $"[VarInt] encoding does not match type '{fqType}'" }));
                return null;
            }
        }
        else if (fqType == "global::SpanForge.ByteSlice")
        {
            if (lengthPrefix.HasValue)
            {
                kind = MemberKind.LengthPrefixedSlice;
            }
            else if (fixedBytes.HasValue)
            {
                kind = MemberKind.FixedSlice;
            }
            else
            {
                diagnostics.Add(new DiagnosticInfo(InvalidAttributeDiagnostic, new object[] { name, "ByteSlice member requires [FixedBytes(n)] or [LengthPrefixed(...)]" }));
                return null;
            }
        }
        else if (type.GetAttributes().Any(a =>
            a.AttributeClass?.ToDisplayString() == BinaryStructAttr ||
            a.AttributeClass?.ToDisplayString() == BinaryUnionAttr))
        {
            kind = MemberKind.NestedBinaryStruct;
        }
        else
        {
            kind = type.SpecialType switch
            {
                SpecialType.System_Boolean => MemberKind.BoolByte,
                SpecialType.System_Byte => MemberKind.U8,
                SpecialType.System_SByte => MemberKind.I8,
                SpecialType.System_UInt16 => MemberKind.U16,
                SpecialType.System_Int16 => MemberKind.I16,
                SpecialType.System_UInt32 => MemberKind.U32,
                SpecialType.System_Int32 => MemberKind.I32,
                SpecialType.System_UInt64 => MemberKind.U64,
                SpecialType.System_Int64 => MemberKind.I64,
                SpecialType.System_Single => MemberKind.F32,
                SpecialType.System_Double => MemberKind.F64,
                _ => MemberKind.Invalid,
            };

            if (kind == MemberKind.Invalid)
            {
                diagnostics.Add(new DiagnosticInfo(UnsupportedMemberDiagnostic, new object[] { name, fqType }));
                return null;
            }
        }

        return new MemberModel(
            Name: name,
            TypeName: fqType,
            Kind: kind,
            Endianness: endianness,
            Magic: magic,
            LengthPrefix: lengthPrefix ?? 1,
            FixedBytes: fixedBytes ?? 0,
            Align: align ?? 0,
            Pad: pad ?? 0,
            Bits: bits ?? 0,
            AssertValue: assertValue);
    }

    private static byte[]? ExtractMagic(ImmutableArray<AttributeData> attrs)
    {
        foreach (AttributeData attr in attrs)
        {
            if (attr.AttributeClass?.ToDisplayString() == MagicAttr)
            {
                return ExtractMagicBytes(attr);
            }
        }
        return null;
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

    private static string EmitSource(TypeModel model)
    {
        var sb = new StringBuilder(2048);
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

        sb.Append(indent).AppendLine(model.DeclarationHeader);
        sb.Append(indent).AppendLine("{");
        string bodyIndent = indent + "    ";

        EmitParseMethod(sb, model, bodyIndent);
        sb.AppendLine();
        EmitTryParseMethod(sb, model, bodyIndent);
        sb.AppendLine();
        EmitWriteMethods(sb, model, bodyIndent);
        sb.AppendLine();
        EmitParserStructs(sb, model, bodyIndent);

        sb.Append(indent).AppendLine("}");

        for (int i = model.Containers.Length - 1; i >= 0; i--)
        {
            indent = indent.Substring(0, indent.Length - 4);
            sb.Append(indent).AppendLine("}");
        }

        return sb.ToString();
    }

    private static void EmitParseMethod(StringBuilder sb, TypeModel model, string ind)
    {
        string fq = model.FullyQualifiedName;
        sb.Append(ind).AppendLine("[MethodImpl(MethodImplOptions.AggressiveInlining)]");
        sb.Append(ind).Append("public static global::SpanForge.ParseResult<").Append(fq).AppendLine("> Parse(ref global::SpanForge.ByteCursor cursor)");
        sb.Append(ind).AppendLine("{");
        string inner = ind + "    ";

        sb.Append(inner).AppendLine("var __cp = cursor.Save();");

        if (model.TypeMagic is { Length: > 0 } tm)
        {
            EmitMagicCheck(sb, inner, tm, fq, "__typeMagic");
        }

        int i = 0;
        while (i < model.Members.Length)
        {
            MemberModel m = model.Members[i];

            if (IsBitMember(m.Kind))
            {
                // Coalesce consecutive bitfield members into a single BitCursor block!
                if (m.Pad > 0) EmitPad(sb, inner, m.Pad, fq, $"__pad_{i}");
                if (m.Align > 0) EmitAlign(sb, inner, m.Align, fq, $"__align_{i}");
                if (m.Magic is { Length: > 0 } mm) EmitMagicCheck(sb, inner, mm, fq, $"__magic_{i}");

                int startBitIdx = i;
                while (i < model.Members.Length && IsBitMember(model.Members[i].Kind))
                {
                    if (i > startBitIdx && (model.Members[i].Pad > 0 || model.Members[i].Align > 0 || model.Members[i].Magic is { Length: > 0 }))
                    {
                        break;
                    }
                    i++;
                }

                EmitBitBlock(sb, inner, model.Members, startBitIdx, i, fq);
                continue;
            }

            if (m.Pad > 0) EmitPad(sb, inner, m.Pad, fq, $"__pad_{i}");
            if (m.Align > 0) EmitAlign(sb, inner, m.Align, fq, $"__align_{i}");
            if (m.Magic is { Length: > 0 } mMagic) EmitMagicCheck(sb, inner, mMagic, fq, $"__magic_{i}");

            EmitMemberRead(sb, inner, m, i, fq);

            if (m.AssertValue.HasValue)
            {
                EmitAssertCheck(sb, inner, m, i, fq);
            }

            i++;
        }

        // Construct instance
        if (model.UsePositionalCtor)
        {
            sb.Append(inner).Append("return new global::SpanForge.Parsed<").Append(fq).Append(">(new ").Append(fq).Append('(');
            for (int k = 0; k < model.Members.Length; k++)
            {
                if (k > 0) sb.Append(", ");
                sb.Append("__f_").Append(k);
            }
            sb.AppendLine("));");
        }
        else
        {
            sb.Append(inner).Append("return new global::SpanForge.Parsed<").Append(fq).Append(">(new ").Append(fq).AppendLine();
            sb.Append(inner).AppendLine("{");
            for (int k = 0; k < model.Members.Length; k++)
            {
                sb.Append(inner).Append("    ").Append(model.Members[k].Name).Append(" = __f_").Append(k).AppendLine(",");
            }
            sb.Append(inner).AppendLine("});");
        }

        sb.Append(ind).AppendLine("}");
    }

    private static void EmitMagicCheck(StringBuilder sb, string ind, byte[] bytes, string fq, string varName)
    {
        if (bytes.Length == 1)
        {
            sb.Append(ind).Append("var ").Append(varName).Append(" = cursor.TagByte(").Append(bytes[0]).AppendLine(");");
        }
        else
        {
            sb.Append(ind).Append("var ").Append(varName).Append(" = cursor.Tag([");
            for (int k = 0; k < bytes.Length; k++)
            {
                if (k > 0) sb.Append(", ");
                sb.Append("0x").Append(bytes[k].ToString("X2"));
            }
            sb.AppendLine("]);");
        }
        sb.Append(ind).Append("if (!").Append(varName).Append(".IsParsed) { cursor.Reset(__cp); return ").Append(varName).Append(".PropagateNonParsed<").Append(fq).AppendLine(">(); }");
    }

    private static void EmitPad(StringBuilder sb, string ind, int padBytes, string fq, string varName)
    {
        sb.Append(ind).Append("var ").Append(varName).Append(" = cursor.Skip(").Append(padBytes).AppendLine(");");
        sb.Append(ind).Append("if (!").Append(varName).Append(".IsParsed) { cursor.Reset(__cp); return ").Append(varName).Append(".PropagateNonParsed<").Append(fq).AppendLine(">(); }");
    }

    private static void EmitAlign(StringBuilder sb, string ind, int align, string fq, string varName)
    {
        sb.Append(ind).Append("var ").Append(varName).Append(" = cursor.Align(").Append(align).AppendLine(");");
        sb.Append(ind).Append("if (!").Append(varName).Append(".IsParsed) { cursor.Reset(__cp); return ").Append(varName).Append(".PropagateNonParsed<").Append(fq).AppendLine(">(); }");
    }

    private static void EmitBitBlock(
        StringBuilder sb,
        string ind,
        ImmutableArray<MemberModel> members,
        int startIdx,
        int endIdx,
        string fq)
    {
        string bcVar = $"__bc_{startIdx}";
        sb.Append(ind).Append("var ").Append(bcVar).AppendLine(" = new global::SpanForge.BitCursor(cursor.Remaining, cursor.Position, cursor.Mode);");
        for (int k = startIdx; k < endIdx; k++)
        {
            MemberModel m = members[k];
            string resVar = $"__r_{k}";
            string readCall = m.Kind switch
            {
                MemberKind.BitBool => $"{bcVar}.ReadBool()",
                MemberKind.BitU8 => $"{bcVar}.ReadU8({m.Bits})",
                MemberKind.BitU16 => $"{bcVar}.ReadU16({m.Bits})",
                MemberKind.BitU32 => $"{bcVar}.ReadU32({m.Bits})",
                MemberKind.BitI32 => $"{bcVar}.ReadI32({m.Bits})",
                MemberKind.BitU64 => $"{bcVar}.ReadU64({m.Bits})",
                _ => throw new InvalidOperationException(),
            };

            sb.Append(ind).Append("var ").Append(resVar).Append(" = ").Append(readCall).AppendLine(";");
            sb.Append(ind).Append("if (!").Append(resVar).Append(".TryGetValue(out global::SpanForge.Parsed<").Append(m.TypeName).Append("> __p_").Append(k).Append(")) { cursor.Reset(__cp); return ").Append(resVar).Append(".PropagateNonParsed<").Append(fq).AppendLine(">(); }");
            sb.Append(ind).Append(m.TypeName).Append(" __f_").Append(k).Append(" = __p_").Append(k).AppendLine(".Value;");

            if (m.AssertValue.HasValue)
            {
                EmitAssertCheck(sb, ind, m, k, fq);
            }
        }

        sb.Append(ind).Append(bcVar).AppendLine(".AlignToByte();");
        sb.Append(ind).Append("cursor.Position = ").Append(bcVar).AppendLine(".CurrentByteOffset;");
    }

    private static void EmitMemberRead(StringBuilder sb, string ind, MemberModel m, int idx, string fq)
    {
        string resVar = $"__r_{idx}";
        bool isBe = m.Endianness == 0;
        string suffix = isBe ? "BE" : "LE";

        switch (m.Kind)
        {
            case MemberKind.BoolByte:
                sb.Append(ind).Append("var ").Append(resVar).AppendLine(" = cursor.ReadU8();");
                sb.Append(ind).Append("if (!").Append(resVar).Append(".TryGetValue(out global::SpanForge.Parsed<byte> __p_").Append(idx).Append(")) { cursor.Reset(__cp); return ").Append(resVar).Append(".PropagateNonParsed<").Append(fq).AppendLine(">(); }");
                sb.Append(ind).Append("bool __f_").Append(idx).Append(" = __p_").Append(idx).AppendLine(".Value != 0;");
                return;

            case MemberKind.LengthPrefixedSlice:
            {
                string lpName = m.LengthPrefix switch
                {
                    0 => "U8",
                    1 => "U16",
                    2 => "U32",
                    3 => "VarUInt64",
                    _ => "U16",
                };
                string endianEnum = isBe ? "Big" : "Little";
                sb.Append(ind).Append("var __savedEnd_").Append(idx).AppendLine(" = cursor.Endianness;");
                sb.Append(ind).Append("cursor.Endianness = global::SpanForge.Endianness.").Append(endianEnum).AppendLine(";");
                sb.Append(ind).Append("var ").Append(resVar).Append(" = cursor.ReadLengthPrefixedBytes(global::SpanForge.LengthPrefix.").Append(lpName).AppendLine(");");
                sb.Append(ind).Append("cursor.Endianness = __savedEnd_").Append(idx).AppendLine(";");
                sb.Append(ind).Append("if (!").Append(resVar).Append(".TryGetValue(out global::SpanForge.Parsed<global::SpanForge.ByteSlice> __p_").Append(idx).Append(")) { cursor.Reset(__cp); return ").Append(resVar).Append(".PropagateNonParsed<").Append(fq).AppendLine(">(); }");
                sb.Append(ind).Append("global::SpanForge.ByteSlice __f_").Append(idx).Append(" = __p_").Append(idx).AppendLine(".Value;");
                return;
            }

            case MemberKind.FixedSlice:
                sb.Append(ind).Append("var ").Append(resVar).Append(" = cursor.Take(").Append(m.FixedBytes).AppendLine(");");
                sb.Append(ind).Append("if (!").Append(resVar).Append(".TryGetValue(out global::SpanForge.Parsed<global::SpanForge.ByteSlice> __p_").Append(idx).Append(")) { cursor.Reset(__cp); return ").Append(resVar).Append(".PropagateNonParsed<").Append(fq).AppendLine(">(); }");
                sb.Append(ind).Append("global::SpanForge.ByteSlice __f_").Append(idx).Append(" = __p_").Append(idx).AppendLine(".Value;");
                return;

            case MemberKind.NestedBinaryStruct:
                sb.Append(ind).Append("var ").Append(resVar).Append(" = ").Append(m.TypeName).AppendLine(".Parse(ref cursor);");
                sb.Append(ind).Append("if (!").Append(resVar).Append(".TryGetValue(out global::SpanForge.Parsed<").Append(m.TypeName).Append("> __p_").Append(idx).Append(")) { cursor.Reset(__cp); return ").Append(resVar).Append(".PropagateNonParsed<").Append(fq).AppendLine(">(); }");
                sb.Append(ind).Append(m.TypeName).Append(" __f_").Append(idx).Append(" = __p_").Append(idx).AppendLine(".Value;");
                return;
        }

        string readExpr = m.Kind switch
        {
            MemberKind.U8 => "cursor.ReadU8()",
            MemberKind.I8 => "cursor.ReadI8()",
            MemberKind.U16 => $"cursor.ReadU16{suffix}()",
            MemberKind.I16 => $"cursor.ReadI16{suffix}()",
            MemberKind.U32 => $"cursor.ReadU32{suffix}()",
            MemberKind.I32 => $"cursor.ReadI32{suffix}()",
            MemberKind.U64 => $"cursor.ReadU64{suffix}()",
            MemberKind.I64 => $"cursor.ReadI64{suffix}()",
            MemberKind.F32 => $"cursor.ReadF32{suffix}()",
            MemberKind.F64 => $"cursor.ReadF64{suffix}()",
            MemberKind.VarUInt64 => "cursor.ReadVarUInt64()",
            MemberKind.VarInt64ZigZag => "cursor.ReadVarInt64()",
            MemberKind.QuicVarUInt64 => "cursor.ReadQuicVarInt64()",
            _ => throw new InvalidOperationException(),
        };

        sb.Append(ind).Append("var ").Append(resVar).Append(" = ").Append(readExpr).AppendLine(";");
        sb.Append(ind).Append("if (!").Append(resVar).Append(".TryGetValue(out global::SpanForge.Parsed<").Append(m.TypeName).Append("> __p_").Append(idx).Append(")) { cursor.Reset(__cp); return ").Append(resVar).Append(".PropagateNonParsed<").Append(fq).AppendLine(">(); }");
        sb.Append(ind).Append(m.TypeName).Append(" __f_").Append(idx).Append(" = __p_").Append(idx).AppendLine(".Value;");
    }

    private static void EmitAssertCheck(StringBuilder sb, string ind, MemberModel m, int idx, string fq)
    {
        sb.Append(ind).Append("if ((ulong)__f_").Append(idx).Append(" != ").Append(m.AssertValue!.Value).Append("UL) { cursor.Reset(__cp); return global::SpanForge.Backtrack.At(__cp.Position, global::SpanForge.ErrorKind.Verify, \"Field '").Append(m.Name).Append("' did not match expected value ").Append(m.AssertValue.Value).AppendLine("\"); }");
    }

    private static int GetFixedMemberSize(MemberKind kind) => kind switch
    {
        MemberKind.BoolByte or MemberKind.U8 or MemberKind.I8 => 1,
        MemberKind.U16 or MemberKind.I16 => 2,
        MemberKind.U32 or MemberKind.I32 or MemberKind.F32 => 4,
        MemberKind.U64 or MemberKind.I64 or MemberKind.F64 => 8,
        _ => -1,
    };

    private static bool IsCoalesceableWriteMember(MemberModel m) =>
        m.Align == 0 && GetFixedMemberSize(m.Kind) > 0;

    private static bool IsCoalesceableTryParseMember(MemberModel m) =>
        m.Align == 0 && m.Magic is null && !m.AssertValue.HasValue && GetFixedMemberSize(m.Kind) > 0;

    private static void EmitTryParseMethod(StringBuilder sb, TypeModel model, string ind)
    {
        string fq = model.FullyQualifiedName;
        sb.Append(ind).AppendLine("[MethodImpl(MethodImplOptions.AggressiveInlining)]");
        sb.Append(ind).Append("public static global::SpanForge.FastStatus TryParse(ref global::SpanForge.ByteCursor cursor, out ").Append(fq).AppendLine(" value)");
        sb.Append(ind).AppendLine("{");
        string inner = ind + "    ";

        // Fast-path direct Try* emission whenever all members are supported by zero-GC-ref Try* primitives.
        bool canUseDirectTry = true;
        foreach (MemberModel m in model.Members)
        {
            if (m.Align > 0 || IsBitMember(m.Kind))
            {
                canUseDirectTry = false;
                break;
            }

            bool okKind = m.Kind switch
            {
                MemberKind.BoolByte or MemberKind.U8 or MemberKind.I8 or
                MemberKind.U16 or MemberKind.I16 or
                MemberKind.U32 or MemberKind.I32 or
                MemberKind.U64 or MemberKind.I64 or
                MemberKind.F32 or MemberKind.F64 or
                MemberKind.VarUInt64 or MemberKind.VarInt64ZigZag or MemberKind.QuicVarUInt64 or
                MemberKind.FixedSlice or MemberKind.NestedBinaryStruct => true,
                MemberKind.LengthPrefixedSlice => m.LengthPrefix is 0 or 1,
                _ => false,
            };
            if (!okKind)
            {
                canUseDirectTry = false;
                break;
            }
        }

        if (canUseDirectTry)
        {
            sb.Append(inner).AppendLine("int __start = cursor.Position;");
            if (model.TypeMagic is { Length: > 0 } tm)
            {
                EmitTryMagicCheck(sb, inner, tm, "__tm");
            }

            int k = 0;
            while (k < model.Members.Length)
            {
                // Coalesce consecutive fixed-size members (without Magic/AssertValue/Align) into ONE TryTakeSpan bounds check!
                if (IsCoalesceableTryParseMember(model.Members[k]))
                {
                    int runStart = k;
                    int runBytes = 0;
                    while (k < model.Members.Length && IsCoalesceableTryParseMember(model.Members[k]))
                    {
                        runBytes += model.Members[k].Pad + GetFixedMemberSize(model.Members[k].Kind);
                        k++;
                    }

                    if (k - runStart >= 2)
                    {
                        string srcVar = $"__src_{runStart}";
                        string stVar = $"__rs_{runStart}";
                        sb.Append(inner).Append("var ").Append(stVar).Append(" = cursor.TryTakeSpan(").Append(runBytes).Append(", out global::System.ReadOnlySpan<byte> ").Append(srcVar).AppendLine(");");
                        sb.Append(inner).Append("if (!").Append(stVar).Append(".IsParsed) { cursor.Position = __start; Unsafe.SkipInit(out value); return ").Append(stVar).AppendLine("; }");

                        int off = 0;
                        for (int j = runStart; j < k; j++)
                        {
                            MemberModel rm = model.Members[j];
                            off += rm.Pad;
                            EmitCoalescedSpanRead(sb, inner, rm, j, srcVar, off);
                            off += GetFixedMemberSize(rm.Kind);
                        }
                        continue;
                    }

                    // Single fixed-size member: fall through to single-member emission
                    k = runStart;
                }

                MemberModel m = model.Members[k];
                if (m.Pad > 0)
                {
                    sb.Append(inner).Append("var __pad_").Append(k).Append(" = cursor.TrySkip(").Append(m.Pad).AppendLine(");");
                    sb.Append(inner).Append("if (!__pad_").Append(k).Append(".IsParsed) { cursor.Position = __start; Unsafe.SkipInit(out value); return __pad_").Append(k).AppendLine("; }");
                }

                if (m.Magic is { Length: > 0 } mm)
                {
                    EmitTryMagicCheck(sb, inner, mm, $"__mm_{k}", rollback: true);
                }

                bool isBe = m.Endianness == 0;
                string suffix = isBe ? "BE" : "LE";
                string call = m.Kind switch
                {
                    MemberKind.BoolByte => $"cursor.TryReadBool(out bool __f_{k})",
                    MemberKind.U8 => $"cursor.TryReadU8(out byte __f_{k})",
                    MemberKind.I8 => $"cursor.TryReadI8(out sbyte __f_{k})",
                    MemberKind.U16 => $"cursor.TryReadU16{suffix}(out ushort __f_{k})",
                    MemberKind.I16 => $"cursor.TryReadI16{suffix}(out short __f_{k})",
                    MemberKind.U32 => $"cursor.TryReadU32{suffix}(out uint __f_{k})",
                    MemberKind.I32 => $"cursor.TryReadI32{suffix}(out int __f_{k})",
                    MemberKind.U64 => $"cursor.TryReadU64{suffix}(out ulong __f_{k})",
                    MemberKind.I64 => $"cursor.TryReadI64{suffix}(out long __f_{k})",
                    MemberKind.F32 => $"cursor.TryReadF32{suffix}(out float __f_{k})",
                    MemberKind.F64 => $"cursor.TryReadF64{suffix}(out double __f_{k})",
                    MemberKind.VarUInt64 => $"cursor.TryReadVarUInt64(out ulong __f_{k})",
                    MemberKind.VarInt64ZigZag => $"cursor.TryReadVarInt64(out long __f_{k})",
                    MemberKind.QuicVarUInt64 => $"cursor.TryReadQuicVarInt64(out ulong __f_{k})",
                    MemberKind.FixedSlice => $"cursor.TryTake({m.FixedBytes}, out global::SpanForge.ByteSlice __f_{k})",
                    MemberKind.LengthPrefixedSlice when m.LengthPrefix == 0 => $"cursor.TryReadLengthPrefixedU8(out global::SpanForge.ByteSlice __f_{k})",
                    MemberKind.LengthPrefixedSlice when m.LengthPrefix == 1 => $"cursor.TryReadLengthPrefixedU16{suffix}(out global::SpanForge.ByteSlice __f_{k})",
                    MemberKind.NestedBinaryStruct => $"{m.TypeName}.TryParse(ref cursor, out {m.TypeName} __f_{k})",
                    _ => throw new InvalidOperationException(),
                };

                sb.Append(inner).Append("var __s_").Append(k).Append(" = ").Append(call).AppendLine(";");
                sb.Append(inner).Append("if (!__s_").Append(k).Append(".IsParsed) { cursor.Position = __start; Unsafe.SkipInit(out value); return __s_").Append(k).AppendLine("; }");

                if (m.AssertValue.HasValue)
                {
                    sb.Append(inner).Append("if ((ulong)__f_").Append(k).Append(" != ").Append(m.AssertValue.Value).AppendLine("UL) { cursor.Position = __start; Unsafe.SkipInit(out value); return global::SpanForge.FastStatus.Backtrack; }");
                }

                k++;
            }

            if (model.UsePositionalCtor)
            {
                sb.Append(inner).Append("value = new ").Append(fq).Append('(');
                for (int j = 0; j < model.Members.Length; j++)
                {
                    if (j > 0) sb.Append(", ");
                    sb.Append("__f_").Append(j);
                }
                sb.AppendLine(");");
            }
            else
            {
                sb.Append(inner).Append("value = new ").Append(fq).AppendLine();
                sb.Append(inner).AppendLine("{");
                for (int j = 0; j < model.Members.Length; j++)
                {
                    sb.Append(inner).Append("    ").Append(model.Members[j].Name).Append(" = __f_").Append(j).AppendLine(",");
                }
                sb.Append(inner).AppendLine("};");
            }

            sb.Append(inner).AppendLine("return global::SpanForge.FastStatus.Parsed;");
        }
        else
        {
            sb.Append(inner).AppendLine("var __res = Parse(ref cursor);");
            sb.Append(inner).Append("if (__res.TryGetValue(out global::SpanForge.Parsed<").Append(fq).AppendLine("> __ok))");
            sb.Append(inner).AppendLine("{");
            sb.Append(inner).AppendLine("    value = __ok.Value;");
            sb.Append(inner).AppendLine("    return global::SpanForge.FastStatus.Parsed;");
            sb.Append(inner).AppendLine("}");
            sb.Append(inner).AppendLine("Unsafe.SkipInit(out value);");
            sb.Append(inner).AppendLine("if (__res.TryGetValue(out global::SpanForge.Incomplete __inc))");
            sb.Append(inner).AppendLine("{");
            sb.Append(inner).AppendLine("    return __inc.Needed.TryGetValue(out global::SpanForge.ExactBytes __ex)");
            sb.Append(inner).AppendLine("        ? global::SpanForge.FastStatus.IncompleteBytes(__ex.Count)");
            sb.Append(inner).AppendLine("        : global::SpanForge.FastStatus.IncompleteUnknown;");
            sb.Append(inner).AppendLine("}");
            sb.Append(inner).AppendLine("return __res.IsFailure ? global::SpanForge.FastStatus.Failure : global::SpanForge.FastStatus.Backtrack;");
        }

        sb.Append(ind).AppendLine("}");
    }

    private static void EmitTryMagicCheck(StringBuilder sb, string ind, byte[] bytes, string varName, bool rollback = false)
    {
        if (bytes.Length == 1)
        {
            sb.Append(ind).Append("var ").Append(varName).Append(" = cursor.TryTagByte(").Append(bytes[0]).AppendLine(", out _);");
        }
        else
        {
            sb.Append(ind).Append("var ").Append(varName).Append(" = cursor.TryTag([");
            for (int k = 0; k < bytes.Length; k++)
            {
                if (k > 0) sb.Append(", ");
                sb.Append("0x").Append(bytes[k].ToString("X2"));
            }
            sb.AppendLine("]);");
        }

        sb.Append(ind).Append("if (!").Append(varName).Append(".IsParsed) { ");
        if (rollback) sb.Append("cursor.Position = __start; ");
        sb.Append("Unsafe.SkipInit(out value); return ").Append(varName).AppendLine("; }");
    }

    private static void EmitCoalescedSpanRead(StringBuilder sb, string ind, MemberModel m, int idx, string srcVar, int off)
    {
        string endian = m.Endianness == 0 ? "BigEndian" : "LittleEndian";
        string expr = m.Kind switch
        {
            MemberKind.BoolByte => $"{srcVar}[{off}] != 0",
            MemberKind.U8 => $"{srcVar}[{off}]",
            MemberKind.I8 => $"unchecked((sbyte){srcVar}[{off}])",
            MemberKind.U16 => $"global::System.Buffers.Binary.BinaryPrimitives.ReadUInt16{endian}({srcVar}.Slice({off}, 2))",
            MemberKind.I16 => $"global::System.Buffers.Binary.BinaryPrimitives.ReadInt16{endian}({srcVar}.Slice({off}, 2))",
            MemberKind.U32 => $"global::System.Buffers.Binary.BinaryPrimitives.ReadUInt32{endian}({srcVar}.Slice({off}, 4))",
            MemberKind.I32 => $"global::System.Buffers.Binary.BinaryPrimitives.ReadInt32{endian}({srcVar}.Slice({off}, 4))",
            MemberKind.U64 => $"global::System.Buffers.Binary.BinaryPrimitives.ReadUInt64{endian}({srcVar}.Slice({off}, 8))",
            MemberKind.I64 => $"global::System.Buffers.Binary.BinaryPrimitives.ReadInt64{endian}({srcVar}.Slice({off}, 8))",
            MemberKind.F32 => $"global::System.Buffers.Binary.BinaryPrimitives.ReadSingle{endian}({srcVar}.Slice({off}, 4))",
            MemberKind.F64 => $"global::System.Buffers.Binary.BinaryPrimitives.ReadDouble{endian}({srcVar}.Slice({off}, 8))",
            _ => throw new InvalidOperationException(),
        };

        sb.Append(ind).Append(m.TypeName).Append(" __f_").Append(idx).Append(" = ").Append(expr).AppendLine(";");
    }

    private static void EmitCoalescedSpanWrite(StringBuilder sb, string ind, MemberModel m, string dstVar, int off)
    {
        string endian = m.Endianness == 0 ? "BigEndian" : "LittleEndian";
        switch (m.Kind)
        {
            case MemberKind.BoolByte:
                sb.Append(ind).Append(dstVar).Append('[').Append(off).Append("] = this.").Append(m.Name).AppendLine(" ? (byte)1 : (byte)0;");
                return;
            case MemberKind.U8:
                sb.Append(ind).Append(dstVar).Append('[').Append(off).Append("] = this.").Append(m.Name).AppendLine(";");
                return;
            case MemberKind.I8:
                sb.Append(ind).Append(dstVar).Append('[').Append(off).Append("] = unchecked((byte)this.").Append(m.Name).AppendLine(");");
                return;
            case MemberKind.U16:
                sb.Append(ind).Append("global::System.Buffers.Binary.BinaryPrimitives.WriteUInt16").Append(endian).Append('(').Append(dstVar).Append(".Slice(").Append(off).Append(", 2), this.").Append(m.Name).AppendLine(");");
                return;
            case MemberKind.I16:
                sb.Append(ind).Append("global::System.Buffers.Binary.BinaryPrimitives.WriteInt16").Append(endian).Append('(').Append(dstVar).Append(".Slice(").Append(off).Append(", 2), this.").Append(m.Name).AppendLine(");");
                return;
            case MemberKind.U32:
                sb.Append(ind).Append("global::System.Buffers.Binary.BinaryPrimitives.WriteUInt32").Append(endian).Append('(').Append(dstVar).Append(".Slice(").Append(off).Append(", 4), this.").Append(m.Name).AppendLine(");");
                return;
            case MemberKind.I32:
                sb.Append(ind).Append("global::System.Buffers.Binary.BinaryPrimitives.WriteInt32").Append(endian).Append('(').Append(dstVar).Append(".Slice(").Append(off).Append(", 4), this.").Append(m.Name).AppendLine(");");
                return;
            case MemberKind.U64:
                sb.Append(ind).Append("global::System.Buffers.Binary.BinaryPrimitives.WriteUInt64").Append(endian).Append('(').Append(dstVar).Append(".Slice(").Append(off).Append(", 8), this.").Append(m.Name).AppendLine(");");
                return;
            case MemberKind.I64:
                sb.Append(ind).Append("global::System.Buffers.Binary.BinaryPrimitives.WriteInt64").Append(endian).Append('(').Append(dstVar).Append(".Slice(").Append(off).Append(", 8), this.").Append(m.Name).AppendLine(");");
                return;
            case MemberKind.F32:
                sb.Append(ind).Append("global::System.Buffers.Binary.BinaryPrimitives.WriteSingle").Append(endian).Append('(').Append(dstVar).Append(".Slice(").Append(off).Append(", 4), this.").Append(m.Name).AppendLine(");");
                return;
            case MemberKind.F64:
                sb.Append(ind).Append("global::System.Buffers.Binary.BinaryPrimitives.WriteDouble").Append(endian).Append('(').Append(dstVar).Append(".Slice(").Append(off).Append(", 8), this.").Append(m.Name).AppendLine(");");
                return;
        }
    }

    private static void EmitWriteMethods(StringBuilder sb, TypeModel model, string ind)
    {
        string inner = ind + "    ";
        string defaultEndianEnum = model.DefaultEndianness == 0 ? "Big" : "Little";

        sb.Append(ind).AppendLine("[MethodImpl(MethodImplOptions.AggressiveInlining)]");
        sb.Append(ind).AppendLine("public bool TryWrite(ref global::SpanForge.ByteWriter writer, global::System.ReadOnlySpan<byte> sourceBuffer = default)");
        sb.Append(ind).AppendLine("{");
        sb.Append(inner).AppendLine("var __cp = writer.Save();");

        int i = 0;
        bool typeMagicConsumed = false;

        // If TypeMagic is followed immediately by 1+ fixed-size members, coalesce TypeMagic + all leading fixed-size members into ONE TryReserve!
        if (model.TypeMagic is { Length: > 0 } tm)
        {
            int runEnd = 0;
            int runBytes = tm.Length;
            while (runEnd < model.Members.Length && IsCoalesceableWriteMember(model.Members[runEnd]))
            {
                MemberModel rm = model.Members[runEnd];
                runBytes += rm.Pad + (rm.Magic?.Length ?? 0) + GetFixedMemberSize(rm.Kind);
                runEnd++;
            }

            if (runEnd >= 1)
            {
                typeMagicConsumed = true;
                sb.Append(inner).Append("if (!writer.TryReserve(").Append(runBytes).AppendLine(", out global::System.Span<byte> __dst_0)) { writer.Reset(__cp); return false; }");
                int off = 0;
                for (int b = 0; b < tm.Length; b++)
                {
                    sb.Append(inner).Append("__dst_0[").Append(off++).Append("] = 0x").Append(tm[b].ToString("X2")).AppendLine(";");
                }

                for (int j = 0; j < runEnd; j++)
                {
                    MemberModel rm = model.Members[j];
                    if (rm.Pad > 0)
                    {
                        sb.Append(inner).Append("__dst_0.Slice(").Append(off).Append(", ").Append(rm.Pad).AppendLine(").Clear();");
                        off += rm.Pad;
                    }
                    if (rm.Magic is { Length: > 0 } mm)
                    {
                        for (int b = 0; b < mm.Length; b++)
                        {
                            sb.Append(inner).Append("__dst_0[").Append(off++).Append("] = 0x").Append(mm[b].ToString("X2")).AppendLine(";");
                        }
                    }
                    EmitCoalescedSpanWrite(sb, inner, rm, "__dst_0", off);
                    off += GetFixedMemberSize(rm.Kind);
                }

                i = runEnd;
            }
        }

        if (!typeMagicConsumed && model.TypeMagic is { Length: > 0 } tmUnmerged)
        {
            EmitMagicWrite(sb, inner, tmUnmerged);
        }

        while (i < model.Members.Length)
        {
            MemberModel m = model.Members[i];

            if (IsBitMember(m.Kind))
            {
                if (m.Pad > 0) EmitPadWrite(sb, inner, m.Pad);
                if (m.Align > 0) EmitAlignWrite(sb, inner, m.Align);
                if (m.Magic is { Length: > 0 } mm) EmitMagicWrite(sb, inner, mm);

                int startBitIdx = i;
                while (i < model.Members.Length && IsBitMember(model.Members[i].Kind))
                {
                    if (i > startBitIdx && (model.Members[i].Pad > 0 || model.Members[i].Align > 0 || model.Members[i].Magic is { Length: > 0 }))
                    {
                        break;
                    }
                    i++;
                }

                EmitBitBlockWrite(sb, inner, model.Members, startBitIdx, i);
                continue;
            }

            // Coalesce any contiguous run of >= 2 fixed-size members (or fixed-size member with pad/magic) into ONE TryReserve!
            if (IsCoalesceableWriteMember(m))
            {
                int runStart = i;
                int runBytes = 0;
                int opCount = 0;
                while (i < model.Members.Length && IsCoalesceableWriteMember(model.Members[i]))
                {
                    MemberModel rm = model.Members[i];
                    if (rm.Pad > 0) { runBytes += rm.Pad; opCount++; }
                    if (rm.Magic is { Length: > 0 } rmm) { runBytes += rmm.Length; opCount++; }
                    runBytes += GetFixedMemberSize(rm.Kind);
                    opCount++;
                    i++;
                }

                if (opCount >= 2)
                {
                    string dstVar = $"__dst_{runStart}";
                    sb.Append(inner).Append("if (!writer.TryReserve(").Append(runBytes).Append(", out global::System.Span<byte> ").Append(dstVar).AppendLine(")) { writer.Reset(__cp); return false; }");
                    int off = 0;
                    for (int j = runStart; j < i; j++)
                    {
                        MemberModel rm = model.Members[j];
                        if (rm.Pad > 0)
                        {
                            sb.Append(inner).Append(dstVar).Append(".Slice(").Append(off).Append(", ").Append(rm.Pad).AppendLine(").Clear();");
                            off += rm.Pad;
                        }
                        if (rm.Magic is { Length: > 0 } rmm)
                        {
                            for (int b = 0; b < rmm.Length; b++)
                            {
                                sb.Append(inner).Append(dstVar).Append('[').Append(off++).Append("] = 0x").Append(rmm[b].ToString("X2")).AppendLine(";");
                            }
                        }
                        EmitCoalescedSpanWrite(sb, inner, rm, dstVar, off);
                        off += GetFixedMemberSize(rm.Kind);
                    }
                    continue;
                }

                i = runStart;
            }

            if (m.Pad > 0) EmitPadWrite(sb, inner, m.Pad);
            if (m.Align > 0) EmitAlignWrite(sb, inner, m.Align);
            if (m.Magic is { Length: > 0 } mMagic) EmitMagicWrite(sb, inner, mMagic);

            EmitMemberWrite(sb, inner, m, i);
            i++;
        }

        sb.Append(inner).AppendLine("return true;");
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
        sb.Append(inner).AppendLine("        throw new global::System.InvalidOperationException(\"Failed to write binary value to IBufferWriter<byte>.\");");
        sb.Append(inner).AppendLine("    }");
        sb.Append(inner).AppendLine("    __hint = __next * 2;");
        sb.Append(inner).AppendLine("}");
        sb.Append(ind).AppendLine("}");
    }

    private static void EmitMagicWrite(StringBuilder sb, string ind, byte[] bytes)
    {
        if (bytes.Length == 1)
        {
            sb.Append(ind).Append("if (!writer.TryWriteU8(").Append(bytes[0]).AppendLine(")) { writer.Reset(__cp); return false; }");
            return;
        }

        sb.Append(ind).Append("if (!writer.TryWriteBytes([");
        for (int k = 0; k < bytes.Length; k++)
        {
            if (k > 0) sb.Append(", ");
            sb.Append("0x").Append(bytes[k].ToString("X2"));
        }
        sb.AppendLine("])) { writer.Reset(__cp); return false; }");
    }

    private static void EmitPadWrite(StringBuilder sb, string ind, int padBytes)
    {
        sb.Append(ind).Append("if (!writer.TryWritePad(").Append(padBytes).AppendLine(")) { writer.Reset(__cp); return false; }");
    }

    private static void EmitAlignWrite(StringBuilder sb, string ind, int align)
    {
        sb.Append(ind).Append("if (!writer.TryAlign(").Append(align).AppendLine(")) { writer.Reset(__cp); return false; }");
    }

    private static void EmitBitBlockWrite(
        StringBuilder sb,
        string ind,
        ImmutableArray<MemberModel> members,
        int startIdx,
        int endIdx)
    {
        string bwVar = $"__bw_{startIdx}";
        sb.Append(ind).Append("var ").Append(bwVar).AppendLine(" = writer.CreateBitWriter();");
        for (int k = startIdx; k < endIdx; k++)
        {
            MemberModel m = members[k];
            string call = m.Kind switch
            {
                MemberKind.BitBool => $"{bwVar}.TryWriteBool(this.{m.Name})",
                MemberKind.BitU8 => $"{bwVar}.TryWriteU8({m.Bits}, this.{m.Name})",
                MemberKind.BitU16 => $"{bwVar}.TryWriteU16({m.Bits}, this.{m.Name})",
                MemberKind.BitU32 => $"{bwVar}.TryWriteU32({m.Bits}, this.{m.Name})",
                MemberKind.BitI32 => $"{bwVar}.TryWriteI32({m.Bits}, this.{m.Name})",
                MemberKind.BitU64 => $"{bwVar}.TryWriteU64({m.Bits}, this.{m.Name})",
                _ => throw new InvalidOperationException(),
            };

            sb.Append(ind).Append("if (!").Append(call).AppendLine(") { writer.Reset(__cp); return false; }");
        }

        sb.Append(ind).Append(bwVar).AppendLine(".AlignToByte();");
        sb.Append(ind).Append("writer.Position = ").Append(bwVar).AppendLine(".CurrentByteOffset;");
    }

    private static void EmitMemberWrite(StringBuilder sb, string ind, MemberModel m, int idx)
    {
        bool isBe = m.Endianness == 0;
        string suffix = isBe ? "BE" : "LE";

        switch (m.Kind)
        {
            case MemberKind.BoolByte:
                sb.Append(ind).Append("if (!writer.TryWriteBool(this.").Append(m.Name).AppendLine(")) { writer.Reset(__cp); return false; }");
                return;

            case MemberKind.LengthPrefixedSlice:
            {
                sb.Append(ind).Append("var __sl_").Append(idx).Append(" = this.").Append(m.Name).AppendLine(";");
                sb.Append(ind).Append("if (__sl_").Append(idx).Append(".Offset < 0 || __sl_").Append(idx).Append(".Length < 0 || __sl_").Append(idx).AppendLine(".End > sourceBuffer.Length) { writer.Reset(__cp); return false; }");
                if (m.LengthPrefix == 0)
                {
                    sb.Append(ind).Append("if (!writer.TryWriteLengthPrefixedU8(sourceBuffer.Slice(__sl_").Append(idx).Append(".Offset, __sl_").Append(idx).AppendLine(".Length))) { writer.Reset(__cp); return false; }");
                    return;
                }
                if (m.LengthPrefix == 1)
                {
                    sb.Append(ind).Append("if (!writer.TryWriteLengthPrefixedU16").Append(suffix).Append("(sourceBuffer.Slice(__sl_").Append(idx).Append(".Offset, __sl_").Append(idx).AppendLine(".Length))) { writer.Reset(__cp); return false; }");
                    return;
                }

                string lpName = m.LengthPrefix == 2 ? "U32" : "VarUInt64";
                string endianEnum = isBe ? "Big" : "Little";
                sb.Append(ind).Append("var __savedEnd_").Append(idx).AppendLine(" = writer.Endianness;");
                sb.Append(ind).Append("writer.Endianness = global::SpanForge.Endianness.").Append(endianEnum).AppendLine(";");
                sb.Append(ind).Append("bool __ok_").Append(idx).Append(" = writer.TryWriteLengthPrefixedBytes(global::SpanForge.LengthPrefix.").Append(lpName).Append(", sourceBuffer.Slice(__sl_").Append(idx).Append(".Offset, __sl_").Append(idx).AppendLine(".Length));");
                sb.Append(ind).Append("writer.Endianness = __savedEnd_").Append(idx).AppendLine(";");
                sb.Append(ind).Append("if (!__ok_").Append(idx).AppendLine(") { writer.Reset(__cp); return false; }");
                return;
            }

            case MemberKind.FixedSlice:
                sb.Append(ind).Append("var __sl_").Append(idx).Append(" = this.").Append(m.Name).AppendLine(";");
                sb.Append(ind).Append("if (__sl_").Append(idx).Append(".Length != ").Append(m.FixedBytes).Append(" || __sl_").Append(idx).Append(".Offset < 0 || __sl_").Append(idx).AppendLine(".End > sourceBuffer.Length) { writer.Reset(__cp); return false; }");
                sb.Append(ind).Append("if (!writer.TryWriteBytes(sourceBuffer.Slice(__sl_").Append(idx).Append(".Offset, __sl_").Append(idx).AppendLine(".Length))) { writer.Reset(__cp); return false; }");
                return;

            case MemberKind.NestedBinaryStruct:
                sb.Append(ind).Append("if (!this.").Append(m.Name).AppendLine(".TryWrite(ref writer, sourceBuffer)) { writer.Reset(__cp); return false; }");
                return;
        }

        string writeCall = m.Kind switch
        {
            MemberKind.U8 => $"writer.TryWriteU8(this.{m.Name})",
            MemberKind.I8 => $"writer.TryWriteI8(this.{m.Name})",
            MemberKind.U16 => $"writer.TryWriteU16{suffix}(this.{m.Name})",
            MemberKind.I16 => $"writer.TryWriteI16{suffix}(this.{m.Name})",
            MemberKind.U32 => $"writer.TryWriteU32{suffix}(this.{m.Name})",
            MemberKind.I32 => $"writer.TryWriteI32{suffix}(this.{m.Name})",
            MemberKind.U64 => $"writer.TryWriteU64{suffix}(this.{m.Name})",
            MemberKind.I64 => $"writer.TryWriteI64{suffix}(this.{m.Name})",
            MemberKind.F32 => $"writer.TryWriteF32{suffix}(this.{m.Name})",
            MemberKind.F64 => $"writer.TryWriteF64{suffix}(this.{m.Name})",
            MemberKind.VarUInt64 => $"writer.TryWriteVarUInt64(this.{m.Name})",
            MemberKind.VarInt64ZigZag => $"writer.TryWriteVarInt64(this.{m.Name})",
            MemberKind.QuicVarUInt64 => $"writer.TryWriteQuicVarInt64(this.{m.Name})",
            _ => throw new InvalidOperationException(),
        };

        sb.Append(ind).Append("if (!").Append(writeCall).AppendLine(") { writer.Reset(__cp); return false; }");
    }

    private static void EmitParserStructs(StringBuilder sb, TypeModel model, string ind)
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

    private static bool IsBitMember(MemberKind kind) => kind is
        MemberKind.BitBool or MemberKind.BitU8 or MemberKind.BitU16 or
        MemberKind.BitU32 or MemberKind.BitI32 or MemberKind.BitU64;

    private enum MemberKind
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
        VarUInt64,
        VarInt64ZigZag,
        QuicVarUInt64,
        LengthPrefixedSlice,
        FixedSlice,
        BitBool,
        BitU8,
        BitU16,
        BitU32,
        BitI32,
        BitU64,
        NestedBinaryStruct,
    }

    private sealed class GenerationResult
    {
        public GenerationResult(TypeModel? model, ImmutableArray<DiagnosticInfo> diagnostics)
        {
            Model = model;
            Diagnostics = diagnostics;
        }

        public TypeModel? Model { get; }
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

    private sealed class TypeModel
    {
        public TypeModel(
            string Namespace,
            string HintName,
            string TypeName,
            string FullyQualifiedName,
            string DeclarationHeader,
            ImmutableArray<ContainerInfo> Containers,
            int DefaultEndianness,
            byte[]? TypeMagic,
            bool UsePositionalCtor,
            ImmutableArray<MemberModel> Members)
        {
            this.Namespace = Namespace;
            this.HintName = HintName;
            this.TypeName = TypeName;
            this.FullyQualifiedName = FullyQualifiedName;
            this.DeclarationHeader = DeclarationHeader;
            this.Containers = Containers;
            this.DefaultEndianness = DefaultEndianness;
            this.TypeMagic = TypeMagic;
            this.UsePositionalCtor = UsePositionalCtor;
            this.Members = Members;
        }

        public string Namespace { get; }
        public string HintName { get; }
        public string TypeName { get; }
        public string FullyQualifiedName { get; }
        public string DeclarationHeader { get; }
        public ImmutableArray<ContainerInfo> Containers { get; }
        public int DefaultEndianness { get; }
        public byte[]? TypeMagic { get; }
        public bool UsePositionalCtor { get; }
        public ImmutableArray<MemberModel> Members { get; }
    }

    private sealed class MemberModel
    {
        public MemberModel(
            string Name,
            string TypeName,
            MemberKind Kind,
            int Endianness,
            byte[]? Magic,
            int LengthPrefix,
            int FixedBytes,
            int Align,
            int Pad,
            int Bits,
            ulong? AssertValue)
        {
            this.Name = Name;
            this.TypeName = TypeName;
            this.Kind = Kind;
            this.Endianness = Endianness;
            this.Magic = Magic;
            this.LengthPrefix = LengthPrefix;
            this.FixedBytes = FixedBytes;
            this.Align = Align;
            this.Pad = Pad;
            this.Bits = Bits;
            this.AssertValue = AssertValue;
        }

        public string Name { get; }
        public string TypeName { get; }
        public MemberKind Kind { get; }
        public int Endianness { get; }
        public byte[]? Magic { get; }
        public int LengthPrefix { get; }
        public int FixedBytes { get; }
        public int Align { get; }
        public int Pad { get; }
        public int Bits { get; }
        public ulong? AssertValue { get; }
    }
}
