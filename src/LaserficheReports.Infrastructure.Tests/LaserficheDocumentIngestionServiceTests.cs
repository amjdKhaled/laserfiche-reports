using System.Text.Json;
using LaserficheReports.Domain.Entities;
using LaserficheReports.Infrastructure.Services;
using Xunit;

namespace LaserficheReports.Infrastructure.Tests;

public sealed class LaserficheDocumentIngestionServiceTests
{
    [Fact]
    public void BuildMetadata_PreservesLaserficheIdentityAndFields()
    {
        var entry = new LFEntry
        {
            Id = 608,
            Name = "00043445_00043449",
            FullPath = @"\Scans\00043445_00043449",
            FolderPath = @"\Scans",
            EntryType = LFEntryType.Document,
            TemplateId = 11,
            TemplateName = "SASO",
            PageCount = 1
        };
        LFFieldValue[] fields =
        [
            new() { FieldDefinitionId = 42, FieldName = "Employee ID", Value = "43445" }
        ];

        var json = LaserficheDocumentIngestionService.BuildMetadata("testemployee", entry, fields);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("laserfiche-reports", root.GetProperty("source").GetString());
        Assert.Equal("document-metadata", root.GetProperty("record_type").GetString());
        Assert.Equal("testemployee", root.GetProperty("repository_id").GetString());
        Assert.Equal(608, root.GetProperty("entry_id").GetInt32());
        Assert.Equal("metadata-only", root.GetProperty("ingestion_status").GetString());
        Assert.Equal("Employee ID", root.GetProperty("fields")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void BuildMetadataContent_IncludesDocumentAndPopulatedFieldsOnly()
    {
        var entry = new LFEntry { Name = "Document A", FullPath = @"\HR\Document A" };
        LFFieldValue[] fields =
        [
            new() { FieldName = "Department", Value = "HR" },
            new() { FieldName = "Empty", Value = " " }
        ];

        var content = LaserficheDocumentIngestionService.BuildMetadataContent(entry, fields);

        Assert.Contains("Document: Document A", content);
        Assert.Contains("Department: HR", content);
        Assert.DoesNotContain("Empty:", content);
    }

    [Fact]
    public void BuildIndexedContent_AppendsLaserficheTextInPageOrder()
    {
        var entry = new LFEntry { Name = "Document A", FullPath = @"\HR\Document A" };
        LFFieldValue[] fields = [new() { FieldName = "Department", Value = "HR" }];
        LaserficheDocumentIngestionService.IndexedPageText[] pages =
        [
            new(2, "Second page", "ocr"),
            new(1, "First page", "laserfiche")
        ];

        var content = LaserficheDocumentIngestionService.BuildIndexedContent(entry, fields, pages);

        Assert.Contains("Department: HR", content);
        Assert.True(content.IndexOf("Page 1:", StringComparison.Ordinal) <
                    content.IndexOf("Page 2:", StringComparison.Ordinal));
        Assert.Contains("First page", content);
        Assert.Contains("Second page", content);
    }

    [Theory]
    [InlineData(0, 0, "none")]
    [InlineData(1, 0, "laserfiche")]
    [InlineData(0, 1, "ocr")]
    [InlineData(1, 1, "mixed")]
    public void ResolveTextSource_ReportsTheSourcesUsed(
        int laserfichePages,
        int ocrPages,
        string expected)
    {
        Assert.Equal(
            expected,
            LaserficheDocumentIngestionService.ResolveTextSource(laserfichePages, ocrPages));
    }

    [Fact]
    public void ResolvePageNumbers_ProbesFirstPage_WhenLaserficheReportsNoPages()
    {
        var pages = LaserficheDocumentIngestionService.ResolvePageNumbers(0, []);

        Assert.Equal(new[] { 1 }, pages);
    }

    [Fact]
    public void ResolvePageNumbers_UsesDiscoveredPages_WhenReportedCountIsMissing()
    {
        LFDocumentPage[] discovered =
        [
            new() { PageNumber = 3 },
            new() { PageNumber = 1 },
            new() { PageNumber = 3 }
        ];

        var pages = LaserficheDocumentIngestionService.ResolvePageNumbers(null, discovered);

        Assert.Equal(new[] { 1, 3 }, pages);
    }

    [Fact]
    public void ResolveContentDiagnostic_ExplainsEmptyOcrResult()
    {
        var diagnostic = LaserficheDocumentIngestionService.ResolveContentDiagnostic(
            hasUsableText: false,
            detectedPageCount: 1,
            ocrAttemptCount: 1,
            ocrTextPageCount: 0,
            contentFailureCount: 0);

        Assert.Contains("returned no usable text", diagnostic);
    }

    [Fact]
    public void BuildMetadata_RecordsOcrPageProvenance()
    {
        var entry = new LFEntry { Id = 609, Name = "Scanned page", PageCount = 1 };
        LaserficheDocumentIngestionService.IndexedPageText[] pages =
        [
            new(1, "Extracted locally", "ocr")
        ];

        var json = LaserficheDocumentIngestionService.BuildMetadata(
            "testemployee",
            entry,
            [],
            "content-indexed",
            "ocr",
            textPageCount: 1,
            laserficheTextPageCount: 0,
            ocrTextPageCount: 1,
            textPages: pages);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("ocr", root.GetProperty("text_source").GetString());
        Assert.Equal(1, root.GetProperty("ocr_text_page_count").GetInt32());
        Assert.Equal(1, root.GetProperty("text_pages")[0].GetProperty("page_number").GetInt32());
        Assert.Equal("ocr", root.GetProperty("text_pages")[0].GetProperty("source").GetString());
    }

    [Fact]
    public void BuildMetadata_RecordsOcrCorrectionProvenance()
    {
        var entry = new LFEntry { Id = 618, Name = "Arabic regulation", PageCount = 1 };

        var json = LaserficheDocumentIngestionService.BuildMetadata(
            "testemployee",
            entry,
            [],
            ocrCorrectionAttemptCount: 1,
            ocrCorrectedPageCount: 1,
            ocrCorrectionModel: "qwen2.5:7b");

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("ocr_correction_attempt_count").GetInt32());
        Assert.Equal(1, root.GetProperty("ocr_corrected_page_count").GetInt32());
        Assert.Equal("qwen2.5:7b", root.GetProperty("ocr_correction_model").GetString());
    }

    [Fact]
    public void PageTextChunker_PreservesPageAndOffsetsWithOverlap()
    {
        var text = string.Join(' ', Enumerable.Repeat("Arabic English searchable text.", 20));
        LaserficheDocumentIngestionService.IndexedPageText[] pages =
        [
            new(3, text, "ocr")
        ];

        var chunks = PageTextChunker.Split(pages, chunkSize: 200, overlap: 40);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk =>
        {
            Assert.Equal(3, chunk.PageNumber);
            Assert.Equal("ocr", chunk.Source);
            Assert.False(string.IsNullOrWhiteSpace(chunk.Content));
            Assert.True(chunk.EndOffset > chunk.StartOffset);
        });
        Assert.True(chunks[1].StartOffset < chunks[0].EndOffset);
    }

    [Fact]
    public void BuildChunkMetadata_IncludesEvidenceReferences()
    {
        var entry = new LFEntry
        {
            Id = 608,
            Name = "Purchase order",
            FullPath = @"\Purchasing\Purchase order"
        };
        var chunk = new PageTextChunker.TextChunk(2, 1, 100, 240, "ocr", "chunk text");

        var json = LaserficheDocumentIngestionService.BuildChunkMetadata(
            "testemployee", entry, 607, chunk, 4, "nomic-embed-text-v2-moe", 768);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("document-chunk", root.GetProperty("record_type").GetString());
        Assert.Equal(608, root.GetProperty("entry_id").GetInt32());
        Assert.Equal(607, root.GetProperty("parent_document_id").GetInt64());
        Assert.Equal(1, root.GetProperty("page_number").GetInt32());
        Assert.Equal(2, root.GetProperty("chunk_index").GetInt32());
        Assert.Equal(768, root.GetProperty("embedding_dimensions").GetInt32());
    }

    [Fact]
    public void BuildVectorLiteral_UsesPostgresCompatibleInvariantFormat()
    {
        var literal = LaserficheDocumentIngestionService.BuildVectorLiteral([1.5f, -0.25f, 0f]);

        Assert.Equal("[1.5,-0.25,0]", literal);
    }

    [Fact]
    public void ParseEmbeddings_AcceptsOllamaBatchResponse()
    {
        const string response = """
            {"embeddings":[[0.1,0.2,0.3],[0.4,0.5,0.6]]}
            """;

        var embeddings = OllamaTextEmbeddingService.ParseEmbeddings(response, 3, "test-model");

        Assert.Equal(2, embeddings.Count);
        Assert.Equal(new[] { 0.1f, 0.2f, 0.3f }, embeddings[0]);
        Assert.Equal(new[] { 0.4f, 0.5f, 0.6f }, embeddings[1]);
    }

    [Fact]
    public void ParseEmbeddings_RejectsWrongDimensions()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            OllamaTextEmbeddingService.ParseEmbeddings(
                "{\"embeddings\":[[0.1,0.2]]}",
                3,
                "test-model"));

        Assert.Contains("returned 2 dimensions", exception.Message);
    }
}
