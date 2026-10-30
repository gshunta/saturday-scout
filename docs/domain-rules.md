# Domain rules

- Sports are college football and NFL. Each slate is tied to one sport, season year, and provider week. A published slate has exactly ten distinct games from that same tuple; cross-week games are disallowed.
- A slate has one lock instant. Publishing requires that instant to be in the future and no later than the earliest kickoff. Editing and submission stop at the lock instant, including when the explicit `Lock` transition has not yet run.
- A draft sheet may have zero to ten picks with distinct games and distinct ranks in 1–10. Submission requires all ten slate games and all ranks 1–10. An incomplete or unsubmitted sheet earns no weekly entry; standings handling follows in a later phase.
- A pick can select only a participating home or away team. A submitted sheet cannot be edited or submitted twice. The `Version` counter changes on successful edits and submission; persistence will use it for optimistic concurrency.
- A game is scored only with authoritative `Final` status and nonnegative scores. A tie, canceled game, or no-contest is void and awards zero. Scheduled, in-progress, and postponed games are pending and award zero until concluded.
- Correct picks award their confidence rank; incorrect picks award zero. The maximum for one sheet is 55. Score calculation is pure and can be repeated after a corrected final score.
- All instants have UTC offset zero. Provider names and external IDs are stored separately from application IDs. Database uniqueness, foreign keys, transactional saves, and league membership checks belong to later phases.

## Decisions to confirm before API and standings work

- The one-lock-per-slate policy and zero points for missed or incomplete entries are the current domain assumptions.
- Tie breakers for equal leaderboard totals and post-lock visibility of other members' picks remain undecided.
- Score corrections are allowed in the domain; a later scoring phase must audit them when persisted.
