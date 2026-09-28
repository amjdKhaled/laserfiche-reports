#!/usr/bin/env python3
"""Run a LangGraph batch to ingest explicitly selected Laserfiche Entry IDs."""
import argparse
import json
from typing import TypedDict
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

from langgraph.graph import END, START, StateGraph


class State(TypedDict, total=False):
    entry_ids: list[int]
    results: list[dict]


def create_sync_graph(base_url: str):
    def ingest(state: State):
        results = []
        for entry_id in state["entry_ids"]:
            request = Request(f"{base_url}/api/ingestion/laserfiche/{entry_id}",
                              data=b"", method="POST")
            try:
                with urlopen(request, timeout=3600) as response:
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


def main():
    parser = argparse.ArgumentParser(description="Ingest selected Entry IDs locally using LangGraph.")
    parser.add_argument("entry_ids", nargs="+", type=int)
    args = parser.parse_args()
    if any(entry_id < 1 for entry_id in args.entry_ids):
        parser.error("Entry IDs must be positive.")
    result = create_sync_graph("http://127.0.0.1:5187").invoke({"entry_ids": args.entry_ids})
    print(json.dumps(result["results"], ensure_ascii=False, indent=2))
    if any(item["status"] == "failed" for item in result["results"]):
        raise SystemExit(1)


if __name__ == "__main__":
    main()
