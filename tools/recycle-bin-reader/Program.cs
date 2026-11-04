using System;
using System.Collections.Generic;
using System.Text;
using Laserfiche.RepositoryAccess;
using Newtonsoft.Json;

// A single authenticated, read-only SDK operation per child process. Credentials arrive on stdin,
// never command-line arguments, configuration, files or diagnostics. No restore/purge API is called.
internal static class Program
{
    private sealed class Input
    {
        public string RepositoryId { get; set; }
        public string RepositoryName { get; set; }
        public string ServerName { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public bool Secure { get; set; } = true;
        public int MaxRows { get; set; } = 100000;
    }
    private static int Main()
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = new UTF8Encoding(false);
        try { return Read(); }
        catch (Exception error)
        {
            // Do not print native exception messages: they may contain request/identity details.
            Console.Error.WriteLine(error.GetType().Name);
            Console.Write(JsonConvert.SerializeObject(new { error = "sdk_read_failed" }));
            return 1;
        }
    }
    private static int Read()
    {
        var text = Console.In.ReadToEnd();
        if (text.Length > 32768) throw new ArgumentException("Input too large");
        var input = JsonConvert.DeserializeObject<Input>(text, new JsonSerializerSettings {
            MissingMemberHandling = MissingMemberHandling.Error, MaxDepth = 8 });
        if (input == null || string.IsNullOrWhiteSpace(input.Username) || input.Username.Length > 256 ||
            string.IsNullOrEmpty(input.Password) || input.Password.Length > 4096 ||
            string.IsNullOrWhiteSpace(input.RepositoryId) || string.IsNullOrWhiteSpace(input.RepositoryName) ||
            string.IsNullOrWhiteSpace(input.ServerName) || input.MaxRows < 1 || input.MaxRows > 100000)
            throw new ArgumentException("Invalid input");
        using (var session = new Session())
        {
            session.IsSecure = input.Secure;
            session.LogIn(input.Username, input.Password, new RepositoryRegistration(input.ServerName, input.RepositoryName));
            var rows = new List<object>();
            using (var listing = RecycleBin.GetListing(RecycleBinColumn.Name, SortDirection.Ascending, session))
            {
                var total = listing.RowCount;
                if (total < 0) throw new InvalidOperationException("Unknown listing size");
                if (total > input.MaxRows)
                {
                    Console.Write(JsonConvert.SerializeObject(new { error = "row_limit" }));
                    return 2;
                }
                for (var start = 1; start <= total; start += 200)
                {
                    var count = Math.Min(200, total - start + 1);
                    var batch = listing.GetRowsData(start, count);
                    if (batch.Length != count) throw new InvalidOperationException("Incomplete listing");
                    foreach (var row in batch)
                    {
                        // A deleted page or electronic file of a surviving document is NOT a deleted document.
                        if (row.ObjectType != RecycleBinObjectType.DeletedEntry ||
                            row.EntryType != EntryType.Document && row.EntryType != EntryType.Folder) continue;
                        rows.Add(new { entryId = row.EntryId, name = row.Name,
                            entryType = row.EntryType == EntryType.Document ? "document" : "folder",
                            deletedBy = row.DeleterName,
                            deletedAt = DateTime.SpecifyKind(row.DeletedTimeUtc, DateTimeKind.Utc),
                            pageCount = row.EntryType == EntryType.Document ? (int?)row.PageCount : null });
                    }
                }
                if (listing.RowCount != total) throw new InvalidOperationException("Listing changed");
            }
            Console.Write(JsonConvert.SerializeObject(new { protocol = "recycle-bin-v1", repositoryId = input.RepositoryId,
                username = input.Username, scope = "current_user_visible", complete = true, entries = rows }));
        }
        return 0;
    }
}
