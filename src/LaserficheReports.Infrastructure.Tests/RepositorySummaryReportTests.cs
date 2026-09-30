using LaserficheReports.Web;
using Xunit;

namespace LaserficheReports.Infrastructure.Tests;

public sealed class RepositorySummaryReportTests
{
    [Theory]
    [InlineData("لخصلي جميع الوثائق الموجودة في المخزن")]
    [InlineData("لخص جميع الملفات في المستودع")]
    [InlineData("لخص أهم النقاط في الوثائق المفهرسة")]
    [InlineData("اعرض كل مستندات المخزن")]
    [InlineData("كم عدد الملفات في المستودع")]
    public void WholeRepositoryQuestionsDoNotUseTopK(string question) =>
        Assert.True(ReportsQuestionScope.IsWholeRepository(question));

    [Theory]
    [InlineData("لخص وثيقة 618")]
    [InlineData("اعطيني الملفات الموجودة في ال qa")]
    [InlineData("ما الوثائق المتعلقة بطلب إجازة")]
    public void SpecificQuestionsAreNotRepositoryOverviews(string question) =>
        Assert.False(ReportsQuestionScope.IsWholeRepository(question));

    [Fact]
    public void All73DocumentsAreRepresentedOnceIncludingUnindexedDocument()
    {
        var documents = Enumerable.Range(1, 73).Select(id =>
            new RepositorySummaryItem(id, $"وثيقة {id}", "إدارية", "تحت الإجراء",
                id != 73, id == 1)).ToArray();
        var report = RepositorySummaryReport.Render(documents, "لخص جميع الوثائق في المخزن")
            .Replace("\r\n", "\n");
        Assert.Contains("73 وثيقة", report);
        Assert.Contains("72 مفهرسة، و1 غير مفهرسة", report);
        Assert.Contains("نص صفحات مفهرس لـ 1 وثيقة", report);
        foreach (var item in documents)
            Assert.Equal(1, report.Split($"• {item.Name} — ID {item.EntryId}\n").Length - 1);
        Assert.Contains("غير مفهرسة؛ لا يتوفر ملخص لمحتوى الصفحات", report);
    }

    [Fact]
    public void ConflictingValuesAreExplicitAndDistinctWorkflowFieldsStaySeparate()
    {
        var summary = RepositorySummaryReport.SummarizeFields(new (string, string?)[]
        {
            ("حالة الوثيقة", "جاهز للاتلاف"),
            ("حالة  الوثيقة", "جاهز للنشر"),
            ("إجراء الوثيقة", "تم رفض فهرسة الوثيقة")
        });
        Assert.Contains("تعارض في حالة الوثيقة", summary);
        Assert.Contains("جاهز للاتلاف / جاهز للنشر", summary);
        Assert.Contains("إجراء الوثيقة: تم رفض فهرسة الوثيقة", summary);
    }

    [Fact]
    public void MissingDocumentDuringLiveScanIsReportedAsIncomplete()
    {
        var report = RepositorySummaryReport.Render([
            new RepositorySummaryItem(1, "وثيقة", "", "", true, false)
        ], "لخص جميع الوثائق", 2);
        Assert.Contains("الملخص غير مكتمل", report);
        Assert.Contains("تعذر إدراج 1", report);
    }

    [Fact]
    public void MissingFieldsDoNotProduceInventedContent() =>
        Assert.Contains("لا تتوفر", RepositorySummaryReport.SummarizeFields([]));
}
