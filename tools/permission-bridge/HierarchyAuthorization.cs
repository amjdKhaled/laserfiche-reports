#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;

// SDK-independent policy, also compiled into the security test suite.
internal sealed class HierarchyNode
{
    internal readonly int Id, ParentId;
    internal readonly bool IsFolder, CanBrowseAndRead;
    internal HierarchyNode(int id, int parentId, bool isFolder, bool canBrowseAndRead)
    { Id = id; ParentId = parentId; IsFolder = isFolder; CanBrowseAndRead = canBrowseAndRead; }
}
internal sealed class HierarchyResult
{
    internal readonly string Status;
    internal readonly int[] Chain;
    internal HierarchyResult(string status, int[] chain = null) { Status = status; Chain = chain; }
}
internal static class HierarchyAuthorization
{
    internal const int MaximumDepth = 256;
    internal static HierarchyResult Evaluate(int entryId, int rootId, Func<int, HierarchyNode> read)
    {
        if (entryId <= 0 || rootId <= 0) return new HierarchyResult("VerificationUnavailable");
        var nodes = new List<HierarchyNode>();
        var visited = new HashSet<int>();
        var id = entryId;
        for (var depth = 0; depth < MaximumDepth; depth++)
        {
            if (id <= 0 || !visited.Add(id)) return new HierarchyResult("VerificationUnavailable");
            var node = read(id); // SDK exceptions are caught by the process boundary, never interpreted as absence.
            if (node == null || node.Id != id || nodes.Count > 0 && !node.IsFolder)
                return new HierarchyResult("VerificationUnavailable");
            if (!node.CanBrowseAndRead)
                return new HierarchyResult(nodes.Count > 0 ? "AncestorAccessDenied" :
                    node.IsFolder ? "FolderAccessDenied" : "DocumentAccessDenied");
            nodes.Add(node);
            if (node.Id == rootId)
            {
                if (!node.IsFolder) return new HierarchyResult("VerificationUnavailable");
                // Detect moves and revocations observed during the check. This is not an atomic LF transaction.
                foreach (var previous in nodes)
                {
                    var current = read(previous.Id);
                    if (current == null || current.Id != previous.Id || current.ParentId != previous.ParentId ||
                        current.IsFolder != previous.IsFolder || !current.CanBrowseAndRead)
                        return new HierarchyResult("VerificationUnavailable");
                }
                return new HierarchyResult("Accessible", nodes.Select(n => n.Id).ToArray());
            }
            id = node.ParentId;
        }
        return new HierarchyResult("VerificationUnavailable");
    }
}
