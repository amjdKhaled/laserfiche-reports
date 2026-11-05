namespace LaserficheReports.Application.Interfaces;

public interface IFullHierarchyPolicy
{
    bool HasIncompleteVerification => false;
    void MarkIncompleteVerification() { }
}
