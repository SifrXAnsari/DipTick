-- DipTick's session store. user_version steps when the tables change: 2 added session.pinned; 3
-- added a chat's own table and brief, the tables for its rules, tool calls, sources, files,
-- connectors and skills, and 'tool' among a message's kinds; 4 let a source be one a provider read
-- or a search gave, as well as one fetched or cited. SqliteChatStore steps an older file up first.
-- Ids are checked in code and by the trigger, never by a CHECK naming seats.
PRAGMA user_version = 4;

CREATE TABLE IF NOT EXISTS session (
  id              TEXT PRIMARY KEY,
  title           TEXT NOT NULL DEFAULT '',
  person          TEXT NOT NULL,
  mode            TEXT NOT NULL,
  budget_json     TEXT NOT NULL,
  spent_json      TEXT NOT NULL,
  created_utc     TEXT NOT NULL,
  updated_utc     TEXT NOT NULL,
  status          TEXT NOT NULL DEFAULT 'open' CHECK (status IN ('open','closed')),
  lease_pid       INTEGER,
  lease_until_utc TEXT,
  pinned          INTEGER NOT NULL DEFAULT 0,
  brief           TEXT NOT NULL DEFAULT ''
);

-- A chat's table, as history: a row for each change from the line it took effect. The newest row of
-- each seat says whether it still sits (seated) and with what model; profile is the set-up agent it is
-- (main, or a seat's section), since one agent can sit twice with two models.
CREATE TABLE IF NOT EXISTS seat (
  session_id  TEXT NOT NULL REFERENCES session(id) ON DELETE CASCADE,
  seat        TEXT NOT NULL,
  from_seq    INTEGER NOT NULL,
  name        TEXT NOT NULL,
  provider    TEXT NOT NULL,
  model       TEXT NOT NULL,
  host        TEXT NOT NULL,
  remote      INTEGER NOT NULL,
  lane_json   TEXT NOT NULL,
  price_in    TEXT,
  price_out   TEXT,
  profile     TEXT NOT NULL DEFAULT '',
  seated      INTEGER NOT NULL DEFAULT 1,
  position    INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (session_id, seat, from_seq)
);

CREATE TABLE IF NOT EXISTS round (
  session_id  TEXT NOT NULL REFERENCES session(id) ON DELETE CASCADE,
  n           INTEGER NOT NULL,
  mode        TEXT NOT NULL,
  plan_json   TEXT NOT NULL,
  opened_utc  TEXT NOT NULL,
  closed_utc  TEXT,
  end_reason  TEXT,
  PRIMARY KEY (session_id, n)
);

CREATE TABLE IF NOT EXISTS message (
  id               INTEGER PRIMARY KEY,
  session_id       TEXT NOT NULL REFERENCES session(id) ON DELETE CASCADE,
  seq              INTEGER,
  round_n          INTEGER NOT NULL,
  step             INTEGER NOT NULL,
  author           TEXT NOT NULL,
  kind             TEXT NOT NULL CHECK (kind IN ('say','pass','answer','keep','share','mode','notice','draft','review','synth','vote','work','check','digest','tool')),
  addressed        TEXT,
  status           TEXT NOT NULL CHECK (status IN ('streaming','done','passed','cut','stopped','failed','denied','repeat','refused')),
  visible_to       TEXT NOT NULL DEFAULT 'all',
  reply_to         INTEGER,
  motion_id        TEXT,
  text             TEXT NOT NULL,
  briefing         TEXT,
  tokens_in        INTEGER,
  tokens_out       INTEGER,
  tokens_estimated INTEGER NOT NULL DEFAULT 0,
  cost_micros      INTEGER,
  provider_cost    TEXT,
  audit_hash       TEXT,
  started_utc      TEXT NOT NULL,
  ended_utc        TEXT,
  UNIQUE (session_id, seq)
);

CREATE TRIGGER IF NOT EXISTS message_author BEFORE INSERT ON message
WHEN NEW.author NOT IN ('person','diptick')
 AND NOT EXISTS (SELECT 1 FROM seat WHERE session_id = NEW.session_id AND seat = NEW.author)
BEGIN SELECT RAISE(ABORT, 'unknown author'); END;

CREATE TABLE IF NOT EXISTS motion (
  session_id TEXT NOT NULL REFERENCES session(id) ON DELETE CASCADE, id TEXT NOT NULL,
  kind TEXT NOT NULL CHECK (kind IN ('motion','work')), voters_json TEXT NOT NULL, max_revisions INTEGER NOT NULL,
  synth_order_json TEXT, goal_seq INTEGER NOT NULL, worst_json TEXT NOT NULL, state TEXT NOT NULL, end_reason TEXT,
  approval_audit_hash TEXT, opened_utc TEXT NOT NULL, closed_utc TEXT, PRIMARY KEY (session_id, id));

CREATE TABLE IF NOT EXISTS proposal (
  session_id TEXT NOT NULL, motion_id TEXT NOT NULL, id TEXT NOT NULL, version INTEGER NOT NULL,
  parent_hash TEXT, hash TEXT NOT NULL, author TEXT NOT NULL, based_on_json TEXT, title TEXT NOT NULL,
  clauses_json TEXT NOT NULL, items_json TEXT NOT NULL, why TEXT, source_seq INTEGER NOT NULL,
  state TEXT NOT NULL CHECK (state IN ('current','superseded','withdrawn','approved','rejected')),
  audit_hash TEXT NOT NULL, created_utc TEXT NOT NULL, PRIMARY KEY (session_id, id, version));

CREATE TABLE IF NOT EXISTS shown (
  session_id TEXT NOT NULL, seat TEXT NOT NULL, turn_seq INTEGER NOT NULL,
  proposal TEXT NOT NULL, version INTEGER NOT NULL, hash TEXT NOT NULL,
  PRIMARY KEY (session_id, seat, turn_seq, proposal, version));

CREATE TABLE IF NOT EXISTS act (
  id INTEGER PRIMARY KEY, session_id TEXT NOT NULL, motion_id TEXT, seq INTEGER NOT NULL,
  seat TEXT NOT NULL, kind TEXT NOT NULL, target TEXT, hash TEXT, where_ref TEXT, quote TEXT, text TEXT,
  accepted INTEGER NOT NULL, code TEXT, audit_hash TEXT NOT NULL);

CREATE TABLE IF NOT EXISTS box (
  session_id TEXT NOT NULL REFERENCES session(id) ON DELETE CASCADE, id TEXT NOT NULL,
  motion_id TEXT, text TEXT NOT NULL, text_hash TEXT NOT NULL,
  state TEXT NOT NULL CHECK (state IN ('proposed','open','claimed','ticked','struck','needs-person')),
  flagged INTEGER NOT NULL DEFAULT 0, owner TEXT, source TEXT NOT NULL, tick_id TEXT, needs_json TEXT,
  disputes INTEGER NOT NULL DEFAULT 0, rev INTEGER NOT NULL, updated_utc TEXT NOT NULL, PRIMARY KEY (session_id, id));

CREATE TABLE IF NOT EXISTS box_event (
  id INTEGER PRIMARY KEY, session_id TEXT NOT NULL, box TEXT NOT NULL, tick_id TEXT,
  event TEXT NOT NULL CHECK (event IN ('propose','accept','open','claim','confirm','dispute','flag','reopen',
                                        'escalate','strike','restore','tick','ticked','assign','refused')),
  actor TEXT NOT NULL, from_state TEXT, to_state TEXT, source_seq INTEGER, quote TEXT, reason TEXT, code TEXT,
  audit_hash TEXT NOT NULL, at_utc TEXT NOT NULL);

-- A chat's own rules ("Allow for this chat"). A chat narrows the permissions; it never loosens a deny.
CREATE TABLE IF NOT EXISTS chat_policy (
  session_id TEXT NOT NULL REFERENCES session(id) ON DELETE CASCADE,
  rule       TEXT NOT NULL,
  verdict    TEXT NOT NULL CHECK (verdict IN ('allow','ask','deny')),
  set_utc    TEXT NOT NULL,
  audit_hash TEXT NOT NULL,
  PRIMARY KEY (session_id, rule));

-- The skills on in a chat, each with the hash the person approved.
CREATE TABLE IF NOT EXISTS chat_skill (
  session_id TEXT NOT NULL REFERENCES session(id) ON DELETE CASCADE,
  skill      TEXT NOT NULL,
  hash       TEXT NOT NULL,
  on_utc     TEXT NOT NULL,
  PRIMARY KEY (session_id, skill));

-- The connectors on in a chat.
CREATE TABLE IF NOT EXISTS chat_connector (
  session_id TEXT NOT NULL REFERENCES session(id) ON DELETE CASCADE,
  connector  TEXT NOT NULL,
  on_utc     TEXT NOT NULL,
  PRIMARY KEY (session_id, connector));

-- One row per tool call, its arguments kept only as a digest.
CREATE TABLE IF NOT EXISTS tool_call (
  id          INTEGER PRIMARY KEY,
  session_id  TEXT NOT NULL REFERENCES session(id) ON DELETE CASCADE,
  seq         INTEGER,
  seat        TEXT NOT NULL,
  connector   TEXT,
  tool        TEXT NOT NULL,
  args_digest TEXT NOT NULL,
  status      TEXT NOT NULL CHECK (status IN ('asked','allowed','denied','done','failed')),
  audit_hash  TEXT,
  started_utc TEXT NOT NULL,
  ended_utc   TEXT);

-- The pages a chat's answers used: fetched by DipTick, cited or read by a provider, or given by a search.
CREATE TABLE IF NOT EXISTS source (
  session_id  TEXT NOT NULL REFERENCES session(id) ON DELETE CASCADE,
  id          INTEGER NOT NULL,
  seq         INTEGER,
  address     TEXT NOT NULL,
  title       TEXT NOT NULL DEFAULT '',
  kind        TEXT NOT NULL CHECK (kind IN ('fetched','cited','provider','search')),
  fetched_utc TEXT,
  sha256      TEXT,
  text        TEXT,
  PRIMARY KEY (session_id, id));

-- The files a person or an agent brought into a chat.
CREATE TABLE IF NOT EXISTS attachment (
  id          INTEGER PRIMARY KEY,
  session_id  TEXT NOT NULL REFERENCES session(id) ON DELETE CASCADE,
  seq         INTEGER,
  author      TEXT NOT NULL,
  name        TEXT NOT NULL,
  type        TEXT NOT NULL,
  size        INTEGER NOT NULL,
  sha256      TEXT NOT NULL,
  bytes       BLOB,
  saved_path  TEXT,
  created_utc TEXT NOT NULL);

CREATE INDEX IF NOT EXISTS message_session ON message (session_id, id);
CREATE INDEX IF NOT EXISTS session_updated ON session (status, updated_utc);
CREATE INDEX IF NOT EXISTS act_session ON act (session_id, seq);
