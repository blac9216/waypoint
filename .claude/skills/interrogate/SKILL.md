---
name: interrogate
description: Interrogate the owner about a plan, design, decision or idea until nothing is left silently assumed — in numbered rounds, one question per turn, with the agent's recommendation on every question and a written decision record at the end. Use it whenever the owner says "grill me", "let's grill this", "interrogate the plan", "stress-test this", asks to flesh out a design together, or when another skill (planning, skill design, decomposition) needs owner decisions before it can act. Never use it to ask for facts you could look up yourself.
argument-hint: <topic> [--record <where the decision record goes>]
---

# Interrogate

A tool other skills call, or the owner invokes ad hoc. Its job is to turn a fuzzy plan into
a set of decisions the owner actually made, one at a time, without ever putting two
decisions in one question and without ever asking the owner for something you could find
yourself. It ends with a decision record the caller told you where to put.

## Before the first question

1. **Where does the record go?** The calling skill or the owner says (an epic thread, a
   file in a skill folder, a milestone ledger, scratch only). If nobody said, **ask this
   first**, before any design question — it is the one question that is not about the
   design.
2. **Classify the ceremony** and say it out loud so the owner can override: *spike* (a
   feasibility question — a couple of questions, no record beyond the answer), *bounded*
   (a change inside an existing flow — one short round), *architectural* (new subsystem
   or shape — full rounds). Ceremony scales; the owner's confirmation at the end never
   does.
3. **Think the design through end to end** and build the **design tree**: every decision
   and the decisions that hang off it. Look up every *fact* the tree needs (files, APIs,
   current state) — dispatch lookups if they are slow, and only the questions downstream
   of a lookup wait for it. Facts are your job; **decisions are the owner's**.
4. Write the questions to a scratch file grouped into related categories. Only the
   **frontier** goes into round 1: questions whose prerequisites are already settled.

## A round

Open the round with the total number of questions and the count per category. Then ask
them **one per turn**, in order, each using the fixed template in
[references/question-template.md](references/question-template.md): a header line
(round · question x/N · category · queued-for-next-round count), the **Previous Answer**
as you understood it, a table of newly queued follow-ups *only if that answer produced
one*, then the question with its options and your recommendation marked.

Rules that make the rounds work:

- **One decision per question.** If you catch yourself joining two asks with "and", split
  them. The owner answers the sentence they read; a fused question gets half an answer.
- **Plain terms, first reading.** The owner should understand the question without a
  second pass; define a term in a clause if you must use it.
- **Options plus a recommendation, always.** Put the recommended option first and mark it.
  "Other" is always available to the owner; you do not need to list it.
- **Record as you go.** Every answer, clarification and aside goes into the scratch record
  under its question the moment it is given — not at the end of the round.
- **Follow-ups wait.** An answer that raises a new question queues it for the next round;
  you finish the current round first. (This is the frontier rule: the new question's
  prerequisite was just settled, so it belongs to the next frontier, not this one.)
- **The Previous Answer line is a check, not a courtesy.** State what you understood in
  one line; if the owner corrects it, fix the record and re-ask anything that depended on
  the misreading.
- **Skip what is already answered.** If a later answer settles a queued question, say so
  in one line and move on; the tally still counts it.

## Sidebar

When the owner says **"sidebar"** (or asks a question back instead of answering), the
round pauses and you switch to **litigation mode**: answer the question fully, argue the
trade-offs, look up what needs looking up, propose. You do **not** return to the question
round until the owner explicitly indicates they agree with the resolution ("yep", "that
covers it", "ok go on"). Record the resolution under the question that spawned it. A
sidebar often changes queued questions — re-evaluate them before continuing.

## Between rounds

When the round's tally is complete: evaluate every queued follow-up against the answers
given (drop the ones already answered), add anything the sidebars raised, and open the
next round with its count and categories. Repeat until the frontier is empty — every
branch of the tree visited, nothing silently assumed.

## Closing

1. **Self-review the record** with fresh eyes before the owner sees it: placeholders,
   internal contradictions between answers, scope creep beyond the topic, any answer that
   could be read two ways. Fix what you can; list what you cannot.
2. **Write the decision record** where it belongs
   ([references/record-template.md](references/record-template.md)) and present the
   summary in chat.
3. **Confirm shared understanding** — the owner says the record is right — before the
   calling skill (or you) acts on it. Nothing built on an unconfirmed record.

## Why it is shaped this way

The owner reads one question at a time with full attention and answers precisely; a
wall of questions gets skimmed and half-answered. The count and tally exist because not
knowing how long an interrogation will last is its own cost. The recommendation exists
because the agent has already thought it through and withholding that wastes the owner's
time. Recording as you go exists because context compacts. The sidebar lock exists because
a question answered mid-argument is a question the owner will re-open later.
