#!/usr/bin/env python3
"""Run a local LangGraph ingestion batch for selected or all accessible entries."""
import argparse
import http.cookiejar
import json
import os
from collections import deque
from typing import TypedDict
from urllib.error import HTTPError, URLError
from urllib.request import HTTPCookieProcessor, Request, build_opener

from langgraph.graph import END, START, StateGraph


class State(TypedDict, total=False):
    entry_ids: list[int]
    results: list[dict]


def create_sync_graph(base_url: str, opener):
    def ingest(state: State):
        results = []
        for entry_id in state["entry_ids"]:
            request = Request(f"{base_url}/api/ingestion/laserfiche/{entry_id}",
                              data=b"", method="POST")
            try:
                with opener.open(request, timeout=3600) as response:
                    result = json.load(response)
                    results.append({"entryId": entry_id, "status": "ok",
                                    "ingestionStatus": result.get("ingestionStatus"),
                                    "chunkCount": result.get("chunkCount", 0)})
            except (HTTPError, URLError, TimeoutError) as error:
                results.append({"entryId": entry_id, "status": "failed", "error": str(error)})
        return {"results": results}

    graph = StateGraph(State)
    graph.add_node("ingest_selected_entries", ingest)
    graph.add_edge(START, "ingest_selected_entries")
    graph.add_edge("ingest_selected_entries", END)
    return graph.compile()


def discover_all(base_url: str, opener):
    """Walk every accessible folder; never treat an incomplete listing as success."""
    folders = deque([0])
    seen_folders = {0}
    seen_documents = set()
    repository = None
    while folders:
        folder_id = folders.popleft()
        with opener.open(f"{base_url}/api/reports/repository/folders/{folder_id}/children",
                         timeout=180) as response:
            page = json.load(response)
        if not isinstance(page, dict) or not isinstance(page.get("folders"), list) or not isinstance(page.get("documents"), list):
            raise ValueError(f"Incomplete folder response for {folder_id}.")
        if not isinstance(page.get("repositoryId"), str) or not page["repositoryId"]:
            raise ValueError(f"Repository identity missing for folder {folder_id}.")
        if repository is not None and page["repositoryId"].lower() != repository.lower():
            raise ValueError("Repository changed during traversal.")
        repository = page["repositoryId"]
        for folder in page["folders"]:
            child_id = folder.get("id") if isinstance(folder, dict) else None
            if not isinstance(child_id, int) or child_id < 1:
                raise ValueError(f"Invalid child folder in {folder_id}.")
            if child_id not in seen_folders:
                seen_folders.add(child_id)
                folders.append(child_id)
        for document in page["documents"]:
            entry_id = document.get("id") if isinstance(document, dict) else None
            if not isinstance(entry_id, int) or entry_id < 1:
                raise ValueError(f"Invalid document in {folder_id}.")
            seen_documents.add(entry_id)
    return sorted(seen_documents)


def main():
    parser = argparse.ArgumentParser(description="Ingest selected Entry IDs locally using LangGraph.")
    parser.add_argument("entry_ids", nargs="*", type=int)
    parser.add_argument("--all", action="store_true", help="Discover every accessible document recursively.")
    args = parser.parse_args()
    if not args.all and not args.entry_ids:
        parser.error("Provide Entry IDs or --all.")
    if any(entry_id < 1 for entry_id in args.entry_ids):
        parser.error("Entry IDs must be positive.")
    username = os.environ.get("LF_USERNAME")
    password = os.environ.get("LF_PASSWORD")
    if not username or password is None:
        parser.error("Set LF_USERNAME and LF_PASSWORD in this PowerShell session.")
    base_url = "http://127.0.0.1:5187"
    opener = build_opener(HTTPCookieProcessor(http.cookiejar.CookieJar()))
    login = Request(f"{base_url}/api/session/login",
                    data=json.dumps({"username": username, "password": password}).encode(),
                    headers={"Content-Type": "application/json"}, method="POST")
    try:
        with opener.open(login, timeout=30):
            pass
    except (HTTPError, URLError) as error:
        parser.error(f"Laserfiche login failed: {error}")
    try:
        entry_ids = discover_all(base_url, opener) if args.all else args.entry_ids
    except (HTTPError, URLError, TimeoutError, ValueError) as error:
        parser.error(f"Repository discovery was incomplete: {error}")
    print(f"Discovered {len(entry_ids)} documents for indexing.", flush=True)
    result = create_sync_graph(base_url, opener).invoke({"entry_ids": entry_ids})
    print(json.dumps(result["results"], ensure_ascii=False, indent=2))
    if any(item["status"] == "failed" for item in result["results"]):
        raise SystemExit(1)


if __name__ == "__main__":
    main()
