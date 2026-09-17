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
}
