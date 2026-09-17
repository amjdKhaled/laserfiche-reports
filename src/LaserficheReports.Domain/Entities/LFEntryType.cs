namespace LaserficheReports.Domain.Entities;

/// <summary>
/// Classifies the type of an entry stored in a Laserfiche repository.
/// </summary>
public enum LFEntryType
{
    /// <summary>Entry type could not be determined from the API response.</summary>
    Unknown,

    /// <summary>A file document such as a PDF, Word document, or image.</summary>
    Document,

    /// <summary>A folder that can contain other entries.</summary>
    Folder,

    /// <summary>A shortcut that references another entry in the repository.</summary>
    Shortcut,

    /// <summary>A records management record series container.</summary>
    RecordSeries
}
