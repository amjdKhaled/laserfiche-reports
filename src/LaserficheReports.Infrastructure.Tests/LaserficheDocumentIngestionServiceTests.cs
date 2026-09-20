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
}
