"""Post-hoc vocabulary correction — a prompt-equivalent for engines that lack one.

Whisper accepts a decoder prompt, so `STTClient.cs` feeds it the contact list
and gets biased decoding for free. Parakeet and Moonshine have no such hook,
which is most of why they lose ground on contact names. This closes that gap
from the outside: snap near-miss spans in the transcript onto known vocabulary
("Devon" -> "Devin", "Home Assistance" -> "Home Assistant").

It is applied uniformly to every engine so nobody gets a free ride — whisper
included, which usually gains little because the prompt already did the work.

Deliberately conservative. Over-correction invents contacts that were never
spoken, which is worse than a miss: a wrong-but-plausible name sends a text to
the wrong person, while a garbled one just fails.
"""

import difflib
import re

# Terms the assistant actually cares about beyond contact names. Kept in sync
# with the jargon string in STTClient.cs plus the tool enums those words feed.
DEFAULT_JARGON = [
    "Arduino", "Home Assistant", "PlayStation", "Remote Play", "LED",
    "Spotify", "Chrome", "Visual Studio", "YouTube",
]

# A contact name is only plausible right after an SMS trigger, which is exactly
# how the dispatcher reads it. Inside that window we can afford to be generous;
# outside it we must not be, or ordinary words get snapped onto names
# ("cares" and "Fares" score 0.80 the same as "Ahmed" and "Ahmad").
NAME_TRIGGERS = ("text", "message", "send", "tell", "call", "sms")
NAME_WINDOW = 4          # words after a trigger where a name may appear
IN_CONTEXT_THRESHOLD = 0.75
DEFAULT_THRESHOLD = 0.88

# In-context names are matched on a consonant skeleton rather than raw
# characters, because mis-transcribed names stay phonetically close while
# drifting a long way orthographically. This both catches more and rejects
# more: "fatters"/"Fares" goes 0.67 -> 0.86, while the false friend
# "cares"/"Fares" goes 0.80 -> 0.67.
PHONETIC_THRESHOLD = 0.78

# Short tokens are far too easy to match by accident ("a" -> "Ada").
MIN_TERM_LENGTH = 4


def build_vocabulary(contacts=None, jargon=None):
    """Canonical terms to snap toward, longest first so multiword wins.

    Returns a list of (term, is_contact) — contacts are context-gated, jargon
    is distinctive enough to correct anywhere.
    """
    jargon = list(DEFAULT_JARGON if jargon is None else jargon)
    terms = [(t, True) for t in (contacts or [])] + [(t, False) for t in jargon]

    seen, out = set(), []
    for term, is_contact in terms:
        term = (term or "").strip()
        key = term.lower()
        if term and key not in seen and len(term) >= MIN_TERM_LENGTH:
            seen.add(key)
            out.append((term, is_contact))
    # Longer terms first: "Home Assistant" must be tried before "Home".
    return sorted(out, key=lambda pair: -len(pair[0].split()))


def _similar(a, b):
    return difflib.SequenceMatcher(None, a.lower(), b.lower()).ratio()


def _skeleton(text):
    """Crude consonant skeleton — a poor man's metaphone, no dependencies.

    Drops non-initial vowels and folds the spelling variants that actually
    show up in ASR output for names.
    """
    s = re.sub(r"[^a-z]", "", text.lower())
    s = re.sub(r"(?<=.)[aeiou]+", "", s)
    s = s.replace("ph", "f").replace("ck", "k").replace("q", "k").replace("z", "s")
    return re.sub(r"(.)\1+", r"\1", s)


def _phonetic(a, b):
    ska, skb = _skeleton(a), _skeleton(b)
    if not ska or not skb:
        return 0.0
    return difflib.SequenceMatcher(None, ska, skb).ratio()


def _name_context(words, index):
    """True if word `index` sits shortly after an SMS trigger word."""
    for back in range(1, NAME_WINDOW + 1):
        j = index - back
        if j < 0:
            break
        if words[j].lower() in NAME_TRIGGERS:
            return True
    return False


def correct(text, vocabulary, threshold=DEFAULT_THRESHOLD):
    """Replace near-miss spans with their canonical vocabulary term."""
    if not text or not vocabulary:
        return text

    # Split into words and the separators between them, so punctuation and
    # spacing survive the rewrite untouched.
    tokens = re.findall(r"\w+|\W+", text)
    word_positions = [i for i, t in enumerate(tokens) if t.isalnum() or "'" in t]

    # A span that already IS a vocabulary term must never be rewritten to a
    # similar one. "Amin" and "Amino" are both real contacts and score 0.89
    # against each other, so without this a correct name gets "corrected".
    known = {term.lower() for term, _ in vocabulary}
    max_span = max(len(term.split()) for term, _ in vocabulary)

    i = 0
    while i < len(word_positions):
        words = [tokens[p] for p in word_positions]
        replaced = False

        # Longest span first, so "Home Assistant" beats "Home".
        for span in range(min(max_span, len(word_positions) - i), 0, -1):
            start, end = word_positions[i], word_positions[i + span - 1]
            candidate = "".join(tokens[start:end + 1])
            if candidate.lower() in known or len(candidate) < MIN_TERM_LENGTH:
                continue

            in_context = _name_context(words, i)

            # Score every same-length term and take the BEST one. Taking the
            # first match instead sends "Ahmed" to Hamood (0.86, earlier in the
            # list) rather than Ahmad (1.00).
            best, best_score = None, 0.0
            for term, is_contact in vocabulary:
                if len(term.split()) != span:
                    continue
                limit = IN_CONTEXT_THRESHOLD if (is_contact and in_context) else threshold
                score = _similar(candidate, term)
                if is_contact and in_context:
                    score = max(score, _phonetic(candidate, term))
                    limit = min(limit, PHONETIC_THRESHOLD)
                if score >= limit and score > best_score:
                    best, best_score = term, score

            if best is not None:
                tokens[start:end + 1] = [best]
                word_positions = [j for j, t in enumerate(tokens)
                                  if t.isalnum() or "'" in t]
                i += span
                replaced = True
                break

        if not replaced:
            i += 1

    return "".join(tokens)
