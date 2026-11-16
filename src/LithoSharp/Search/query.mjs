// Search text is normalized separately from document URLs, anchors and API identities.
const queryDocuments = (() => {
  const fold = value => String(value ?? '').normalize('NFKC').toLowerCase().replace(/\s+/gu, ' ').trim();
  const words = value => String(value ?? '').normalize('NFKC')
    .replace(/(\p{Lu})(\p{Lu}\p{Ll})/gu, '$1 $2').replace(/([\p{Ll}\p{Nd}])(\p{Lu})/gu, '$1 $2')
    .toLowerCase().replace(/[^\p{L}\p{N}]+/gu, ' ').trim();
  const text = value => ({exact: fold(value), words: words(value)});
  const contains = (field, token) => field.exact.includes(token) || field.words.includes(token);
  const anchor = value => encodeURIComponent(value).replace(/[!'()*]/gu, character => '%' + character.charCodeAt(0).toString(16).toUpperCase());
  const score = (field, exact, normalized, tokens) =>
    (field.exact === exact ? 120 : field.exact.includes(exact) ? 60 : field.words === normalized ? 30 : field.words.includes(normalized) ? 15 : 0)
    + tokens.filter(token => contains(field, token)).length;
  const excerpt = (body, tokens) => {
    const lower = body.toLowerCase(), positions = tokens.map(token => lower.indexOf(token)).filter(index => index >= 0);
    const start = Math.max(0, (positions.length ? Math.min(...positions) : 0) - 40);
    return body.slice(start, start + 180);
  };
  // Keep the built-in search's subsequence fallback below every direct token match.
  const fuzzyScore = (needle, hay) => {
    let cursor = 0, streak = 0, total = 0;
    for (const character of needle) {
      const at = hay.indexOf(character, cursor);
      if (at < 0) return -1;
      streak += at > 0 && /\s/u.test(hay[at - 1]) ? 3 : 1;
      total += streak; cursor = at + character.length;
    }
    return total;
  };
  return (documents, query, {collection = null, version = null, locale = null, limit = 30, fuzzy = false} = {}) => {
    const exact = fold(query), normalized = words(query), tokens = [...new Set(normalized.split(' ').filter(Boolean))];
    const hits = [];
    for (const document of documents) {
      if (collection !== null && document.collection !== collection || version !== null && document.version !== version || locale !== null && document.locale !== locale) continue;
      if (!tokens.length) {
        const section = document.sections?.[0];
        hits.push({document, section: section ?? null, title: document.title + (section ? ' · ' + section.title : ''),
          url: document.url + (section ? '#' + anchor(section.anchor) : ''), body: excerpt(section?.body ?? document.body ?? '', tokens), score: section ? 1 : 0});
        continue;
      }
      const title = text(document.title), summary = text(document.summary), tags = text((document.tags ?? []).join(' ')), body = text(document.body);
      const sections = (document.sections ?? []).map(section => ({section, title: text(section.title), body: text(section.body)}));
      const fields = [title, summary, tags, body, ...sections.flatMap(section => [section.title, section.body])];
      if (!tokens.every(token => fields.some(field => contains(field, token)))) {
        if (!fuzzy) continue;
        const hay = fields.map(field => field.words).join(' '), values = tokens.map(token => fuzzyScore(token, hay));
        if (values.some(value => value < 0)) continue;
        hits.push({document, section: null, title: document.title, url: document.url, body: excerpt(document.body || document.summary || '', tokens),
          score: Math.min(999, values.reduce((total, value) => total + value, 0) + tokens.reduce((total, token) => total + Math.max(0, fuzzyScore(token, title.words)) * 2, 0))});
        continue;
      }
      let best = null, sectionScore = 0;
      for (const section of sections) {
        if (!tokens.some(token => contains(section.title, token) || contains(section.body, token)) ||
            !tokens.every(token => [title, summary, tags, section.title, section.body].some(field => contains(field, token)))) continue;
        const value = (section.title.exact.includes(exact) || section.body.exact.includes(exact) ? 1000 : 0)
          + score(section.title, exact, normalized, tokens) * 3 + score(section.body, exact, normalized, tokens);
        if (best === null || value > sectionScore) { best = section.section; sectionScore = value; }
      }
      hits.push({document, section: best, title: document.title + (best ? ' · ' + best.title : ''),
        url: document.url + (best ? '#' + anchor(best.anchor) : ''), body: excerpt(best?.body ?? document.body ?? '', tokens),
        score: 1000 + (fields.some(field => field.exact.includes(exact)) ? 10000 : 0)
          + score(title, exact, normalized, tokens) * 4 + score(summary, exact, normalized, tokens) * 2
          + score(tags, exact, normalized, tokens) + score(body, exact, normalized, tokens) + sectionScore});
    }
    return hits.sort((left, right) => right.score - left.score || (left.url < right.url ? -1 : left.url > right.url ? 1 : 0)).slice(0, limit);
  };
})();
