using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace SpanForge.Generators.Tests;

public sealed class GeneratorDiagnosticsTests
{
    private static readonly ImmutableArray<MetadataReference> BaseReferences = BuildBaseReferences();

    private static ImmutableArray<MetadataReference> BuildBaseReferences()
    {
        var list = ImmutableArray.CreateBuilder<MetadataReference>();
        var trustedAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) ?? [];

        foreach (string path in trustedAssemblies)
        {
            list.Add(MetadataReference.CreateFromFile(path));
        }

        string spanForgePath = typeof(BinaryStructAttribute).Assembly.Location;
        if (!trustedAssemblies.Contains(spanForgePath, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(MetadataReference.CreateFromFile(spanForgePath));
        }

        return list.ToImmutable();
    }

    private static GeneratorDriverRunResult RunGenerators(string source)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            assemblyName: "GeneratorTestAssembly",
            syntaxTrees: [tree],
            references: BaseReferences,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators:
            [
                new BinaryStructGenerator().AsSourceGenerator(),
                new BinaryUnionGenerator().AsSourceGenerator(),
            ],
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview));

        driver = driver.RunGenerators(compilation);
        return driver.GetRunResult();
    }

    [Fact]
    public void SFG001_NonPartialBinaryStruct_And_NonPartialContainer_ReportDiagnostic()
    {
        var r1 = RunGenerators("""
            using SpanForge;
            namespace Demo;
            [BinaryStruct]
            public struct NotPartial { public uint Value { get; set; } }
            """);

        Assert.Contains(r1.Diagnostics, d => d.Id == "SFG001");
        Assert.Empty(r1.GeneratedTrees);

        var r2 = RunGenerators("""
            using SpanForge;
            namespace Demo;
            public class OuterNotPartial
            {
                [BinaryStruct]
                public partial struct Inner { public uint Value { get; set; } }
            }
            """);

        Assert.Contains(r2.Diagnostics, d => d.Id == "SFG001");
        Assert.Empty(r2.GeneratedTrees);
    }

    [Fact]
    public void SFG002_UnsupportedMemberType_ReportsDiagnostic()
    {
        var res = RunGenerators("""
            using System;
            using SpanForge;
            namespace Demo;
            [BinaryStruct]
            public partial struct BadMember
            {
                public string Text { get; set; }
                public Guid Id { get; set; }
            }
            """);

        var sfg002 = res.Diagnostics.Where(d => d.Id == "SFG002").ToArray();
        Assert.Equal(2, sfg002.Length);
        Assert.Empty(res.GeneratedTrees);
    }

    [Theory]
    [InlineData("[FixedBytes(-1)] public ByteSlice Data { get; set; }")]
    [InlineData("[Align(3)] public uint Value { get; set; }")]
    [InlineData("[Pad(-2)] public uint Value { get; set; }")]
    [InlineData("[Bits(9)] public byte Value { get; set; }")]
    [InlineData("[Bits(2)] public bool Flag { get; set; }")]
    [InlineData("[VarInt(VarIntEncoding.Leb128SignedZigZag)] public ulong Value { get; set; }")]
    [InlineData("public ByteSlice BareSlice { get; set; }")]
    public void SFG003_InvalidAttributeConfiguration_ReportsDiagnostic(string memberDecl)
    {
        var res = RunGenerators($$"""
            using SpanForge;
            namespace Demo;
            [BinaryStruct]
            public partial struct BadAttr
            {
                {{memberDecl}}
            }
            """);

        Assert.Contains(res.Diagnostics, d => d.Id == "SFG003");
        Assert.Empty(res.GeneratedTrees);
    }

    [Theory]
    [InlineData("""
        [BinaryUnion]
        [UnionCase(1, typeof(uint))]
        public struct NotPartialUnion;
        """)]
    [InlineData("""
        [BinaryUnion]
        public partial struct EmptyUnion;
        """)]
    [InlineData("""
        [BinaryUnion(Endianness.Big, TagWidth.U8)]
        [UnionCase(1, typeof(uint))]
        [UnionCase(1, typeof(ulong))]
        public partial struct DuplicateTagUnion;
        """)]
    [InlineData("""
        [BinaryUnion(Endianness.Big, TagWidth.U8)]
        [UnionCase(1, typeof(uint))]
        [UnionCase(2, typeof(uint))]
        public partial struct DuplicateTypeUnion;
        """)]
    [InlineData("""
        [BinaryUnion(Endianness.Big, TagWidth.U8)]
        [UnionCase(256, typeof(uint))]
        public partial struct OverflowU8TagUnion;
        """)]
    [InlineData("""
        [BinaryUnion(Endianness.Big, TagWidth.U16)]
        [UnionCase(70000, typeof(uint))]
        public partial struct OverflowU16TagUnion;
        """)]
    public void SFG004_InvalidBinaryUnionConfiguration_ReportsDiagnostic(string unionDecl)
    {
        var res = RunGenerators($$"""
            using SpanForge;
            namespace Demo;
            {{unionDecl}}
            """);

        Assert.Contains(res.Diagnostics, d => d.Id == "SFG004");
        Assert.Empty(res.GeneratedTrees);
    }

    [Fact]
    public void SFG005_UnsupportedUnionCasePayload_ReportsDiagnostic()
    {
        var res = RunGenerators("""
            using System;
            using SpanForge;
            namespace Demo;
            [BinaryUnion]
            [UnionCase(1, typeof(string))]
            [UnionCase(2, typeof(DateTime))]
            public partial struct BadPayloadUnion;
            """);

        var sfg005 = res.Diagnostics.Where(d => d.Id == "SFG005").ToArray();
        Assert.Equal(2, sfg005.Length);
        Assert.Empty(res.GeneratedTrees);
    }

    [Fact]
    public void ValidBinaryStruct_And_BinaryUnion_GenerateCleanSourcesWithZeroDiagnostics()
    {
        var res = RunGenerators("""
            using SpanForge;
            namespace Demo;

            [BinaryStruct(Endianness.Big)]
            public readonly partial record struct Ping(uint Nonce);

            [BinaryUnion(Endianness.Big, TagWidth.U8)]
            [UnionCase(1, typeof(Ping))]
            [UnionCase(2, typeof(ushort))]
            public readonly partial struct Packet;
            """);

        Assert.Empty(res.Diagnostics);
        Assert.Equal(2, res.GeneratedTrees.Length);
        string combined = string.Join("\n", res.GeneratedTrees.Select(t => t.GetText().ToString()));
        Assert.Contains("TryWrite(ref global::SpanForge.ByteWriter writer", combined);
        Assert.Contains("WriteTo(global::System.Buffers.IBufferWriter<byte> output", combined);
        Assert.Contains("[global::System.Runtime.CompilerServices.UnionAttribute]", combined);
        Assert.Contains("TryGetValue(out global::Demo.Ping value)", combined);
    }
}
