using LaserficheReports.Web;
using Xunit;

namespace LaserficheReports.Infrastructure.Tests;

public sealed class RepositoryFieldQueryTests
{
    [Fact]
    public void ExactUserQuestionMatchesArabicVariantsAndOnlyTheRequestedField()
    {
        Assert.True(RepositoryFieldQuery.TryParse(
            "ماهي الوثائق الموجود فيها إجراء الوثيقة يساوي تحت الاجراء", out var query));
        Assert.True(query!.MatchesName("إ جراء   الوثيقة"));
        Assert.False(query.MatchesName("حالة الوثيقة"));
        Assert.True(query.MatchesValue("تحت الإجراء"));
        Assert.False(query.MatchesValue("تم رفض فهرسة الوثيقة"));
        Assert.False(query.MatchesValue("كانت تحت الإجراء ثم انتهت"));
    }

    [Fact]
    public void SearchesAll73DocumentsWithoutConfusingBusinessNumberWithEntryId()
    {
        Assert.True(RepositoryFieldQuery.TryParse(
            "ماهي الوثائق الموجود فيها إجراء الوثيقة يساوي تحت الاجراء", out var query));
        var documents = Enumerable.Range(1, 73).Select(id => new
        {
            Id = 500 + id, Name = $"100{id} 7/5/2026",
            Value = id <= 60 ? "تحت الإجراء" : "جاهز للنشر"
        }).ToArray();
        var matches = documents.Where(document => query!.MatchesValue(document.Value)).ToArray();
        Assert.Equal(60, matches.Length);
        Assert.Equal(560, matches.Last().Id);
        Assert.NotEqual(int.Parse(matches.Last().Name.Split(' ')[0]), matches.Last().Id);
    }

    [Theory]
    [InlineData("إجراء الوثيقة = تحت الإجراء؟")]
    [InlineData("ما المستندات التي فيها حالة الوثيقة تساوي جاهز للنشر")]
    public void SupportsEqualityOperators(string question) =>
        Assert.True(RepositoryFieldQuery.TryParse(question, out _));

    [Fact]
    public void ContentQuestionIsNotTreatedAsFieldEquality() =>
        Assert.False(RepositoryFieldQuery.TryParse("ما الوثائق المتعلقة بالصيانة؟", out _));
}

