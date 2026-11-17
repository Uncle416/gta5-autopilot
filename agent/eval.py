"""Measure how well a model understands navigation requests.

Runs every case in agent/evalset.jsonl through the real interpret() pipeline
(rules -> model -> guards) and compares the Decision with the expected one.
No game is needed.

    python -m agent.eval --provider deepseek
    python -m agent.eval --provider qwen --repeat 3      # check consistency
    python -m agent.eval --provider none                 # keyword mode only, as a baseline

A case passes when the kind matches and, for routes, the stop list is exactly right.
"""
import argparse
import json
import os
import sys
import time
from collections import defaultdict

from .gazetteer import Gazetteer
from .llm import LLMChain, chain_from_env
from .nlu import Context, interpret

HERE = os.path.dirname(os.path.abspath(__file__))


def load_cases(path):
    with open(path, encoding="utf-8") as f:
        return [json.loads(line) for line in f if line.strip()]


def check(case, d):
    """Returns '' if the decision is acceptable, else a short reason."""
    exp = case["expect"]
    if case.get("no_llm") and d.source != "rule":
        return f"expected a local rule, got source={d.source}"
    if exp["kind"] == "route":
        if d.kind != "route":
            return f"expected route, got {d.kind}" + (f" ({d.question or d.text})" if d.kind in ("clarify", "failed") else "")
        wanted = exp.get("stops_any") or [exp["stops"]]
        return "" if d.stops in wanted else f"stops {d.stops}, wanted {wanted[0]}"
    if exp["kind"] == "clarify":
        if d.kind != "clarify":
            return f"expected a question, got {d.kind}" + (f" {d.stops}" if d.kind == "route" else "")
        missing = [o for o in exp.get("options_include", []) if o not in (d.option_ids or [])]
        return f"question lacks options {missing}" if missing else ""
    if exp["kind"] == "control":
        if d.kind != "control":
            return f"expected control:{exp['action']}, got {d.kind}"
        return "" if d.action == exp["action"] else f"action {d.action}, wanted {exp['action']}"
    if exp["kind"] == "say":
        return "" if d.kind in ("say", "clarify") else f"expected small talk, got {d.kind}"
    return "bad expectation"


def run(cases, gaz, llm, repeat=1, verbose=True):
    results = []
    for case in cases:
        queue = [{"id": pid, "name": gaz.name(pid)} for pid in case.get("queue", [])]
        ctx = Context(nav=case.get("nav", "idle"), queue=queue,
                      history=[tuple(h) for h in case.get("history", [])],
                      pending=case.get("pending"), pending_option_ids=case.get("pending_option_ids", []),
                      scope_text=case.get("scope_text", ""))
        for attempt in range(repeat):
            started = time.monotonic()
            d = interpret(case["text"], ctx, gaz, llm)
            seconds = time.monotonic() - started
            problem = check(case, d)
            results.append({"id": case["id"], "tag": case["tag"], "text": case["text"], "ok": not problem,
                            "problem": problem, "kind": d.kind, "stops": d.stops, "action": d.action,
                            "source": d.source, "seconds": round(seconds, 2),
                            "provider": getattr(llm, "last_used", None) if d.source == "llm" else None})
            if verbose:
                mark = "PASS" if not problem else "FAIL"
                print(f"[{mark}] {case['id']:<16} {case['text'][:28]:<28} {seconds:5.1f}s  {problem}")
    return results


def summarize(results):
    by_tag = defaultdict(lambda: [0, 0])
    for r in results:
        by_tag[r["tag"]][0] += r["ok"]
        by_tag[r["tag"]][1] += 1
    print("\n类别            通过/总数")
    for tag, (ok, total) in by_tag.items():
        print(f"  {tag:<14} {ok}/{total}")
    ok = sum(r["ok"] for r in results)
    fell_back = sum(r["source"] == "fallback" for r in results)
    times = sorted(r["seconds"] for r in results if r["source"] == "llm")
    print(f"\n总计 {ok}/{len(results)} ({100 * ok / max(len(results), 1):.0f}%)"
          + (f"；其中 {fell_back} 条因模型不可用走了关键词模式" if fell_back else ""))
    if times:
        print(f"模型耗时：中位 {times[len(times) // 2]:.1f}s，最慢 {times[-1]:.1f}s")
    return ok == len(results)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--provider", default="auto", help="deepseek | qwen | none | auto (LLM_ORDER with fallback)")
    ap.add_argument("--cases", default=os.path.join(HERE, "evalset.jsonl"))
    ap.add_argument("--repeat", type=int, default=1)
    ap.add_argument("--only", help="run only cases whose id or tag starts with this")
    ap.add_argument("--out", help="write per-case results to this JSONL file")
    args = ap.parse_args()

    chain = chain_from_env()
    if args.provider == "none":
        llm = None
    elif args.provider == "auto":
        llm = chain if chain.providers else None
    else:
        chosen = [p for p in chain.providers if p.name == args.provider]
        if not chosen:
            print(f"provider '{args.provider}' is not configured: set its API key environment variable (see agent/llm.py)")
            return 2
        llm = LLMChain(chosen)
    print("模型：" + (" → ".join(f"{p.name}({p.model})" for p in llm.providers) if llm else "无（仅规则和关键词模式）"))

    cases = load_cases(args.cases)
    if args.only:
        cases = [c for c in cases if c["id"].startswith(args.only) or c["tag"].startswith(args.only)]
    results = run(cases, Gazetteer(), llm, repeat=args.repeat)
    all_ok = summarize(results)
    if args.out:
        with open(args.out, "w", encoding="utf-8") as f:
            for r in results:
                f.write(json.dumps(r, ensure_ascii=False) + "\n")
        print("结果已写入", args.out)
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
