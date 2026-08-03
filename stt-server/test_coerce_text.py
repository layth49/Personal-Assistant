"""Cases for _coerce_text — the shapes NeMo's transcribe() actually returns.

Run: python stt-server/test_coerce_text.py   (no torch/NeMo needed)

The case that matters is `Hypothesis(text='')`. NeMo returns one for every
silent or noise-only clip, and the endpointer ships plenty of those. Coercing it
with `getattr(out, "text", None) or str(out)` returned the repr of the whole
object as the transcript, which the assistant answered as a user turn:

    RECOGNIZED: Hypothesis(score=0.0, y_sequence=tensor([], ...), text='', ...)
"""
import os
import sys
import types

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

# Import _coerce_text without dragging in torch / NeMo / FastAPI: the module
# imports those lazily, below the point we need.
import ast


def load_coerce_text():
    here = os.path.dirname(os.path.abspath(__file__))
    src = open(os.path.join(here, "parakeet_server.py"), encoding="utf-8").read()
    tree = ast.parse(src)
    for node in tree.body:
        if isinstance(node, ast.FunctionDef) and node.name == "_coerce_text":
            module = types.ModuleType("_shim")
            exec(compile(ast.Module(body=[node], type_ignores=[]), "<shim>", "exec"),
                 module.__dict__)
            return module._coerce_text
    raise SystemExit("_coerce_text not found in parakeet_server.py")


class Hypothesis(object):
    """Stand-in with the same attribute the real one exposes."""
    def __init__(self, text):
        self.text = text

    def __repr__(self):
        return ("Hypothesis(score=0.0, y_sequence=tensor([], dtype=torch.int64), "
                "text={!r}, dec_out=None, length=0)".format(self.text))


class NoText(object):
    def __repr__(self):
        return "SomethingUnexpected(a=1, b=2)"


def main():
    coerce = load_coerce_text()
    failures = []

    def check(expected, value, label):
        actual = coerce(value)
        ok = actual == expected
        if not ok:
            failures.append(label)
        print("{} [{}] -> {!r}".format("ok  " if ok else "FAIL", label, actual))

    # The real failure, both bare and in the list NeMo actually hands back.
    check("", Hypothesis(""), "empty Hypothesis is empty, not its repr")
    check("", [Hypothesis("")], "empty Hypothesis in a list")
    check("", [Hypothesis("   ")], "whitespace-only Hypothesis")

    # Normal traffic.
    check("what time is it", [Hypothesis("what time is it")], "Hypothesis with text")
    check("what time is it", Hypothesis("  what time is it  "), "text is stripped")
    check("what time is it", ["what time is it"], "plain string in a list")
    check("what time is it", "what time is it", "bare string")

    # Degenerate.
    check("", [], "empty list")
    check("", "", "empty string")
    check("", NoText(), "object with no .text falls back to empty, never a repr")

    print("\nALL PASS" if not failures else "\n{} FAILURES".format(len(failures)))
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
