using LaserficheReports.Web;
using Xunit;
namespace LaserficheReports.Web.Tests;
public sealed class QueryRouterTests
{
    [Theory]
    [InlineData("كم عدد الوثائق الموجودة؟", (int)QueryType.RepositoryStatistics)]
    [InlineData("ماهي الوثائق الموجود فيها إجراء الوثيقة يساوي تحت الإجراء؟", (int)QueryType.ExactRepositorySearch)]
    [InlineData("ما القوالب الموجودة؟", (int)QueryType.TemplateQuery)]
    [InlineData("ما الحقول الموجودة؟", (int)QueryType.FieldQuery)]
    [InlineData("اعرض الوثائق داخل مجلد 123", (int)QueryType.FolderQuery)]
    [InlineData("لخص الوثائق التي إجراء الوثيقة فيها تحت الإجراء", (int)QueryType.HybridQuery)]
    [InlineData("لخص جميع الوثائق", (int)QueryType.HybridQuery)]
    [InlineData("كم وثيقة تستخدم قالب X؟", (int)QueryType.TemplateQuery)]
    [InlineData("عدد ملفات PDF", (int)QueryType.RepositoryStatistics)]
    [InlineData("ما أهم المشاكل الموجودة في وثائق إدارة الموارد البشرية؟", (int)QueryType.HybridQuery)]
    [InlineData("ما القرارات المذكورة في المحتوى؟", (int)QueryType.ContentSemanticSearch)]
    public void LiveAndContentQueriesUseDifferentSources(string question,int type)=>Assert.Equal(type,(int)QueryRouter.Route(question).Type);
    [Fact] public void HybridExtractsExactFieldBeforeContentRetrieval()
    {
        var intent=QueryRouter.Route("لخص الوثائق التي إجراء الوثيقة فيها تحت الإجراء");
        Assert.Equal("إجراء الوثيقة",intent.Condition!.FieldQuestion);Assert.Equal("تحت الإجراء",intent.Condition.ExpectedValue);
    }
}
