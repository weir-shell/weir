// Pagefind ranks PAGES, and the one-page reference is long, so its
// per-word density is low and it sinks — and a page hit links to the
// page top. Ranking the heading SECTIONS (Pagefind's sub_results, each
// with its own #anchor) fixes both: a heading that matches the query
// outweighs body text wherever it lives. Shared by the search modal and
// the node check, so the check exercises the shipped ranking.

const PER_PAGE = 3;

const weight = (sub) =>
  (sub.weighted_locations ?? []).reduce(
    (sum, l) => sum + l.balanced_score,
    0,
  );

// exact heading title, then prefix, then substring; Pagefind's score breaks ties
const titleRank = (title, q) => {
  const t = (title ?? "").toLowerCase().replaceAll("`", "");
  return t === q ? 0 : t.startsWith(q) ? 1 : t.includes(q) ? 2 : 3;
};

// pages: the resolved r.data() of each Pagefind result
export function rankSections(pages, query, limit) {
  const q = query.trim().toLowerCase();
  const hits = [];
  for (const page of pages) {
    const subs = (page.sub_results?.length ? page.sub_results : [page])
      .map((sub) => ({
        url: sub.url,
        title: sub.title ?? page.meta?.title ?? page.url,
        page: page.meta?.title ?? page.url,
        excerpt: sub.excerpt ?? page.excerpt,
        rank: titleRank(sub.title, q),
        score: weight(sub),
      }))
      .sort((a, b) => a.rank - b.rank || b.score - a.score)
      .slice(0, PER_PAGE);
    hits.push(...subs);
  }
  hits.sort((a, b) => a.rank - b.rank || b.score - a.score);
  return hits.slice(0, limit);
}
