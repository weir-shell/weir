#include "tree_sitter/parser.h"

// External scanner for `type_param` (`'a`) vs a command-mode raw string
// (`'echo $PPID'`). Both open with `'`; a precedence token cannot tell them
// apart (prec beats length in the lexer, so it would steal short command
// strings). The Rust lifetime-vs-char precedent: peek for a real closing
// quote before the line ends.
//
// The discriminator: a real string CLOSER is a `'` NOT followed by a word
// char; a type-param quote is always followed by a word char (`'a`, `'key`).
// So `type B<'a> = S of 'a | Te` has two quotes both followed by `a` — no
// real closer — and both are type params, not one fake string spanning them.

enum TokenType { TYPE_PARAM };

static inline bool is_ident_start(int32_t c) {
  return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_';
}

static inline bool is_word(int32_t c) {
  return is_ident_start(c) || (c >= '0' && c <= '9');
}

void *tree_sitter_weir_external_scanner_create(void) { return NULL; }
void tree_sitter_weir_external_scanner_destroy(void *payload) {}
unsigned tree_sitter_weir_external_scanner_serialize(void *payload, char *buffer) { return 0; }
void tree_sitter_weir_external_scanner_deserialize(void *payload, const char *buffer, unsigned length) {}

bool tree_sitter_weir_external_scanner_scan(void *payload, TSLexer *lexer,
                                            const bool *valid_symbols) {
  if (!valid_symbols[TYPE_PARAM]) return false;

  while (lexer->lookahead == ' ' || lexer->lookahead == '\t' ||
         lexer->lookahead == '\r' || lexer->lookahead == '\n') {
    lexer->advance(lexer, true);
  }

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
