# Open-session optimizer contract

Session calls encode a stable typed dispatch slot with negative `Call.B`.
Their current target table is not a closed-world proof: a later accepted REPL
generation can add a compatible definition. These checks keep the call opaque
to inlining, native constant evaluation, memory summaries and physical function
compaction at O2/O3, while ordinary arithmetic still folds.

The fixture uses only Core and Optimization; actual cross-generation execution
belongs to the session-dispatch Runtime and compiler journeys. It does not
serialize session metadata or change format-4 closed-program dispatch.
