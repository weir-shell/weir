#include "tree_sitter/alloc.h"
#include "tree_sitter/parser.h"

// External scanner, two duties:
//
// 1. `type_param` (`'a`) vs a command-mode raw string (`'echo $PPID'`).
//    Both open with `'`; a precedence token cannot tell them apart (prec
//    beats length in the lexer, so it would steal short command strings).
//    The Rust lifetime-vs-char precedent: peek for a real closing quote
//    before the line ends.
//
// 2. The `yaml` district [D:yaml-district] — a line-end `yaml` marker
//    followed by an indented block of YAML template lines. The block is
//    NOT weir token soup (the base rules would mis-paint `apps/v1` as
//    identifier-slash-identifier), so the scanner tracks district state
//    and lexes block lines itself: `yaml_key` (text before a real `: `),
//    `yaml_text` (everything else), handing `$name` splices, `$( ... )`
//    holes, `"..."` scalars, `:` and `for` headers back to the internal
//    lexer. Exit is the zero-width hidden `_yaml_end` (the tree-sitter
//    indent-scanner convention): tying the state flip to a SUCCESSFUL
//    scan keeps it consistent under backtracking — mutating state on a
//    false return is not.
//
//    `to yaml` / `from yaml` never reach the marker path at all: the
//    grammar's `adapter` token is an internal single-token match, and
//    longest-match at the `to`/`from` boundary means the scanner is not
//    consulted at the `yaml` position inside it. Remaining over-accepts
//    (a value named `yaml` at line end with an indented next line) are
//    the renderer charter.

enum TokenType { TYPE_PARAM, YAML_MARKER, YAML_KEY, YAML_TEXT, YAML_FOR, YAML_HOLE, YAML_END };

// line modes inside a district
enum { MODE_KEY, MODE_VALUE, MODE_WEIR };

typedef struct {
  char in_district;
  char base;       // indent of the first block line; a shallower line exits
  char mode;       // MODE_*: what the rest of the current line lexes as
  char in_block;   // inside a block scalar's content [D:block-scalars]
  char block_base; // its content indent (0 = not yet seen)
} State;

static inline bool is_ident_start(int32_t c) {
  return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_';
}

static inline bool is_word(int32_t c) {
  return is_ident_start(c) || (c >= '0' && c <= '9');
}

static inline bool is_line_ws(int32_t c) { return c == ' ' || c == '\t'; }
static inline bool is_nl(int32_t c) { return c == '\n' || c == '\r'; }

void *tree_sitter_weir_external_scanner_create(void) {
  State *s = (State *)ts_malloc(sizeof(State));
  s->in_district = 0;
  s->base = 0;
  s->mode = MODE_KEY;
  s->in_block = 0;
  s->block_base = 0;
  return s;
}
void tree_sitter_weir_external_scanner_destroy(void *payload) { ts_free(payload); }
unsigned tree_sitter_weir_external_scanner_serialize(void *payload, char *buffer) {
  State *s = payload;
  buffer[0] = s->in_district;
  buffer[1] = s->base;
  buffer[2] = s->mode;
  buffer[3] = s->in_block;
  buffer[4] = s->block_base;
  return 5;
}
void tree_sitter_weir_external_scanner_deserialize(void *payload, const char *buffer, unsigned length) {
  State *s = payload;
  if (length == 5) {
    s->in_district = buffer[0];
    s->base = buffer[1];
    s->mode = buffer[2];
    s->in_block = buffer[3];
    s->block_base = buffer[4];
  } else {
    s->in_district = 0;
    s->base = 0;
    s->mode = MODE_KEY;
    s->in_block = 0;
    s->block_base = 0;
  }
}

// ---- type_param (unchanged) -------------------------------------------

static bool scan_type_param(TSLexer *lexer) {
  if (lexer->lookahead != '\'') return false;
  lexer->advance(lexer, false);                 // consume the opening '
  if (!is_ident_start(lexer->lookahead)) return false;  // '' or '5 -> not a type param
  while (is_word(lexer->lookahead)) lexer->advance(lexer, false);
  lexer->mark_end(lexer);                        // the token is exactly 'ident

  // scan the rest of the line for a REAL closer (a ' not followed by a word
  // char). A ' followed by a word char is another type-param opener, skipped.
  for (;;) {
    int32_t c = lexer->lookahead;
    if (c == 0 && lexer->eof(lexer)) break;
    if (c == '\n' || c == 0) break;
    if (c == '\'') {
      lexer->advance(lexer, false);
      if (!is_word(lexer->lookahead)) return false;  // real closer -> a string
      continue;                                       // nested 'ident -> keep scanning
    }
    lexer->advance(lexer, false);
  }

  lexer->result_symbol = TYPE_PARAM;
  return true;
}

// ---- yaml district ----------------------------------------------------

// at the word `yaml`: a marker iff it ends its line and the next
// non-blank line is indented (the block). Peeking past mark_end is pure
// lookahead — the token stays exactly `yaml`.
static bool scan_yaml_marker(State *s, TSLexer *lexer) {
  static const char word[] = "yaml";
  for (int i = 0; word[i]; i++) {
    if (lexer->lookahead != word[i]) return false;
    lexer->advance(lexer, false);
  }
  if (is_word(lexer->lookahead)) return false; // yamlish

  // optional ` schema=<name>` suffix [D:yaml-schemas] — part of the
  // marker token, so the whole declaration colours as one keyword
  if (lexer->lookahead == ' ') {
    lexer->mark_end(lexer); // token = `yaml` unless the suffix matches
    lexer->advance(lexer, false);
    static const char kw[] = "schema=";
    int ki = 0;
    while (kw[ki] && lexer->lookahead == kw[ki]) { lexer->advance(lexer, false); ki++; }
    if (kw[ki] == 0) {
      bool any = false;
      while ((lexer->lookahead >= 'a' && lexer->lookahead <= 'z') ||
             (lexer->lookahead >= '0' && lexer->lookahead <= '9') ||
             lexer->lookahead == '-') {
        lexer->advance(lexer, false);
        any = true;
      }
      if (any) lexer->mark_end(lexer); // token = `yaml schema=<name>`
    }
  } else {
    lexer->mark_end(lexer);
  }

  while (is_line_ws(lexer->lookahead)) lexer->advance(lexer, false);
  if (!is_nl(lexer->lookahead)) return false;  // not at line end

  // skip blank lines; the first non-blank line's indent is the base
  for (;;) {
    int32_t c = lexer->lookahead;
    if (c == 0 && lexer->eof(lexer)) return false; // no block
    if (is_nl(c) || is_line_ws(c)) { lexer->advance(lexer, false); continue; }
    break;
  }
  unsigned col = lexer->get_column(lexer);
  if (col == 0) return false;                   // dedented: no block

  s->in_district = 1;
  s->base = (char)(col > 127 ? 127 : col);
  s->mode = MODE_KEY;
  lexer->result_symbol = YAML_MARKER;
  return true;
}

static bool scan_district(State *s, TSLexer *lexer) {
  // consume leading whitespace as skip, watching for a line boundary
  bool saw_nl = false;
  for (;;) {
    int32_t c = lexer->lookahead;
    if (is_line_ws(c)) lexer->advance(lexer, true);
    else if (is_nl(c)) { saw_nl = true; lexer->advance(lexer, true); }
    else break;
  }

  if (lexer->eof(lexer) || (saw_nl && lexer->get_column(lexer) < (unsigned)s->base)) {
    // the district is over: a zero-width exit token carries the state flip
    s->in_district = 0;
    lexer->mark_end(lexer);
    lexer->result_symbol = YAML_END;
    return true;
  }
  // block scalar content [D:block-scalars]: every line is BYTES — one
  // text token, no splice/for/key scanning; a dedent ends the block
  if (s->in_block) {
    if (saw_nl) {
      unsigned col = lexer->get_column(lexer);
      if (s->block_base == 0) s->block_base = (char)(col > 127 ? 127 : col);
      if (col >= (unsigned)s->block_base) {
        bool any = false;
        while (!(is_nl(lexer->lookahead) || (lexer->lookahead == 0 && lexer->eof(lexer)))) {
          lexer->advance(lexer, false);
          any = true;
        }
        if (!any) return false;
        lexer->mark_end(lexer);
        lexer->result_symbol = YAML_TEXT;
        return true;
      }
    }
    s->in_block = 0;
    s->block_base = 0;
  }

  if (saw_nl) s->mode = MODE_KEY;               // a fresh district line

  if (s->mode == MODE_WEIR) return false;       // for-header tail: internal lexes

  int32_t c = lexer->lookahead;
  if (c == '$') {
    // `$( ...` — the hole opener is OUR token so the WEIR flip rides a
    // successful scan (a false return's state mutation is dropped on
    // deserialize — observed, not theorized); the interior then lexes
    // as weir to the line end (holes are single-line: district lines
    // join verbatim, one each)
    lexer->advance(lexer, false);
    if (lexer->lookahead != '(') return false;   // $name splice: internal token
    lexer->advance(lexer, false);
    lexer->mark_end(lexer);
    s->mode = MODE_WEIR;
    lexer->result_symbol = YAML_HOLE;
    return true;
  }
  if (c == '"') {
    if (s->mode == MODE_KEY) s->mode = MODE_VALUE; // quoted key/scalar: string token
    return false;
  }
  if (c == ':') {
    s->mode = MODE_VALUE;                        // post-key colon: punctuation
    return false;
  }

  if (s->mode == MODE_KEY) {
    // `for` header: emit the keyword as OUR token (the WEIR flip must
    // ride a successful scan); the header tail — binder, `in`, source
    // expression — then lexes as weir to the line end
    if (c == 'f') {
      lexer->advance(lexer, false);
      if (lexer->lookahead == 'o') {
        lexer->advance(lexer, false);
        if (lexer->lookahead == 'r') {
          lexer->advance(lexer, false);
          if (is_line_ws(lexer->lookahead)) {
            lexer->mark_end(lexer);
            s->mode = MODE_WEIR;
            lexer->result_symbol = YAML_FOR;
            return true;
          }
        }
      }
      // not `for `: what we consumed joins the key probe below
    }
    // item dashes: `- ` runs emit as text so a following `key:` still keys
    bool consumed = c == 'f'; // partial `for` prefix already consumed
    while (lexer->lookahead == '-') {
      lexer->advance(lexer, false);
      consumed = true;
      if (!is_line_ws(lexer->lookahead)) break;
      while (is_line_ws(lexer->lookahead)) lexer->advance(lexer, false);
      if (lexer->lookahead == '$' || lexer->lookahead == '"' || lexer->lookahead == ':') {
        // splice/quoted follows the dash: emit the dash run alone
        lexer->mark_end(lexer);
        lexer->result_symbol = YAML_TEXT;
        return true;
      }
    }
    // key probe: text up to a real `: ` is a key; no such colon -> scalar.
    // bp tracks the `|`/`|-` header shape (dashes/spaces, pipe, optional
    // minus, trailing spaces) so `- |` arms block mode [D:block-scalars]
    int bp = (c == 'f') ? -1 : 0;
    bool kPrevSpace = true; // token start follows skipped whitespace
    for (;;) {
      int32_t k = lexer->lookahead;
      if (k == '#' && kPrevSpace) {
        // whitespace-preceded #: a comment [D:district-hash]
        if (!consumed) return false;
        lexer->mark_end(lexer);
        s->mode = MODE_VALUE;
        lexer->result_symbol = YAML_TEXT;
        return true;
      }
      if (k == ':' ) {
        lexer->mark_end(lexer);                  // key excludes the colon
        lexer->advance(lexer, false);
        int32_t after = lexer->lookahead;
        if (is_line_ws(after) || is_nl(after) || (after == 0 && lexer->eof(lexer))) {
          if (!consumed) return false;           // bare `:` line: internal
          s->mode = MODE_VALUE;
          lexer->result_symbol = YAML_KEY;
          return true;
        }
        consumed = true;                          // `a:b` — colon is scalar text
        bp = -1;
        continue;
      }
      if (k == '$' || k == '"' || is_nl(k) || (k == 0 && lexer->eof(lexer))) {
        if (!consumed) return false;
        lexer->mark_end(lexer);
        s->mode = MODE_VALUE;
        if (is_nl(k) && (bp == 1 || bp == 2 || bp == 3)) {
          s->in_block = 1;
          s->block_base = 0;
        }
        lexer->result_symbol = YAML_TEXT;
        return true;
      }
      if (bp == 0) bp = (k == '|') ? 1 : ((k == '-' || k == ' ') ? 0 : -1);
      else if (bp == 1) bp = (k == '-') ? 2 : ((k == ' ') ? 3 : -1);
      else if (bp == 2 || bp == 3) bp = (k == ' ') ? 3 : -1;
      kPrevSpace = (k == ' ' || k == '\t');
      lexer->advance(lexer, false);
      consumed = true;
    }
  }

  // MODE_VALUE: scalar text up to a splice, quote, comment, or line
  // end; bp tracks the `|`/`|-` header shape so `key: |` arms block
  // mode; a whitespace-preceded `#` is a comment [D:district-hash] —
  // stop before it and the internal hash_line paints it
  bool consumed = false;
  int bp = 0;
  bool prevSpace = true; // token start follows skipped whitespace
  for (;;) {
    int32_t k = lexer->lookahead;
    if (k == '#' && prevSpace) break;
    if (k == '$' || k == '"' || is_nl(k) || (k == 0 && lexer->eof(lexer))) {
      if (is_nl(k) && (bp == 1 || bp == 2 || bp == 3)) {
        s->in_block = 1;
        s->block_base = 0;
      }
      break;
    }
    if (bp == 0) bp = (k == '|') ? 1 : ((k == ' ') ? 0 : -1);
    else if (bp == 1) bp = (k == '-') ? 2 : ((k == ' ') ? 3 : -1);
    else if (bp == 2 || bp == 3) bp = (k == ' ') ? 3 : -1;
    prevSpace = (k == ' ' || k == '\t');
    lexer->advance(lexer, false);
    consumed = true;
  }
  if (!consumed) return false;
  lexer->mark_end(lexer);
  lexer->result_symbol = YAML_TEXT;
  return true;
}

bool tree_sitter_weir_external_scanner_scan(void *payload, TSLexer *lexer,
                                            const bool *valid_symbols) {
  State *s = payload;

  if (s->in_district && valid_symbols[YAML_TEXT]) return scan_district(s, lexer);

  // outside a district: skip whitespace, then dispatch on the first char
  while (is_line_ws(lexer->lookahead) || is_nl(lexer->lookahead)) {
    lexer->advance(lexer, true);
  }
  if (lexer->lookahead == '\'' && valid_symbols[TYPE_PARAM])
    return scan_type_param(lexer);
  if (lexer->lookahead == 'y' && valid_symbols[YAML_MARKER])
    return scan_yaml_marker(s, lexer);
  return false;
}
