using LaserficheReports.Application.Interfaces;
namespace LaserficheReports.Web.Permissions;
internal sealed class ContentAuthorizationScope : IContentAuthorizationScope
{
    public string Scope { get; } = Guid.NewGuid().ToString("N");
}
