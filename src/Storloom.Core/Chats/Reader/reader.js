"use strict";
(() => {
 const index = window.archiveIndex, labels = index.labels, $ = id => document.getElementById(id);
 let current = null, page = 0, messages = [], request = 0;
 const label = key => labels[key] || key;
 const element = (tag, text, className) => {
  const node = document.createElement(tag);
  if (text) node.textContent = text;
  if (className) node.className = className;
  return node;
 };
 const text = (id, value) => { $(id).textContent = value; };
 document.documentElement.lang = index.language;
 document.title = labels.ChatArchive; text('archive-title', labels.ChatArchive);
 $('contacts-search').placeholder = labels.ReaderSearch; $('message-search').placeholder = labels.ReaderPageSearch;
 $('theme').title = labels.ReaderTheme; text('theme', labels.ReaderTheme);
 text('previous', labels.ReaderPrevious); text('next', labels.ReaderNext);
 text('chat-title', labels.ReaderSelect);
 text('notice', labels.ReaderNotice + (index.unresolvedMedia ? ' ' + label('ReaderUnresolved') + ' ' + index.unresolvedMedia : '') +
  (index.unsupportedMessages ? ' | ' + label('ReaderUnsupportedCount') + ' ' + index.unsupportedMessages : ''));
 let dark = location.hash === '#dark' || (!location.hash && matchMedia('(prefers-color-scheme:dark)').matches);
 document.body.classList.toggle('dark', dark);
 $('theme').onclick = () => { dark = !dark; document.body.classList.toggle('dark', dark); };
 function list() {
  const term = $('contacts-search').value.toLocaleLowerCase(), fragment = document.createDocumentFragment();
  for (const chat of index.conversations) {
   if (!chat.name.toLocaleLowerCase().includes(term)) continue;
   const button = element('button'); button.classList.toggle('active', current?.id === chat.id);
   button.append(element('strong', chat.name), element('small', chat.count + ' ' + labels.ReaderMessages));
   button.onclick = () => { current = chat; page = 0; $('message-search').value = ''; list(); load(); };
   fragment.append(button);
  }
  $('conversations').replaceChildren(fragment);
 }
 function load() {
  if (!current) return;
  const id = ++request;
  text('chat-title', current.name); text('chat-meta', current.count + ' ' + labels.ReaderMessages);
  $('previous').disabled = page === 0; $('next').disabled = page + 1 >= current.pages;
  text('page', (page + 1) + ' / ' + current.pages);
  messages = []; $('messages').replaceChildren();
  const script = element('script'); script.src = 'conversations/' + current.id + '-' + page + '.js';
  script.onload = () => script.remove();
  script.onerror = () => { script.remove(); if (request === id) text('notice', labels.ReaderMissingMedia); };
  document.head.append(script);
 }
 window.receivePage = (conversation, p, items) => {
  if (current?.id !== conversation || p !== page) return;
  messages = items; render();
 };
 const safeAsset = path => /^assets\/[a-f0-9]{64}\.(png|jpg|jpeg|gif|webp|bmp|mp4|webm|mp3|wav|ogg)$/.test(path);
 function renderMedia(media, kind) {
  const node = element(media.Kind === 'video' ? 'video' : media.Kind === 'audio' ? 'audio' : 'img');
  node.src = media.Path;
  if (node.tagName === 'IMG') { node.alt = media.Name; node.loading = 'lazy'; }
  else { node.controls = true; node.preload = 'none'; }
  node.onerror = () => node.replaceWith(element('div', label('ReaderKind_' + kind) + ' · ' + label('ReaderCannotPlay'), 'placeholder'));
  return node;
 }
 function legacyParts(m) {
  const kind = m.Platform === 'WeChat' ? ({3:'image',34:'audio',43:'video',47:'sticker'}[m.Type % 4294967296]) : null;
  if (kind) return [{Kind:kind}];
  const structured = /<[^>]+>|^\s*[\[{]/.test(m.Text) || m.Text.length > 4000;
  return [{Kind:structured?'unsupported':'text',Text:structured?'':m.Text}];
 }
 function sourceDisclosure(message) {
  const details = element('details', '', 'source'), summary = element('summary', label('ReaderSource'));
  details.append(summary);
  // Large source nodes are built only when explicitly requested.
  details.addEventListener('toggle', () => {
   if (!details.open || details.dataset.loaded) return;
   details.dataset.loaded = 'true'; details.append(element('pre', message.Text || ''));
   if (message.SourceText) details.append(element('h4', label('ReaderDatabaseText')), element('pre', message.SourceText));
   for (const [name, value] of [['RawBase64',message.RawBase64],['ExtraRawBase64',message.ExtraRawBase64]]) {
    if (value) details.append(element('h4', name), element('pre', value));
   }
  });
  return details;
 }
 function render() {
  const fragment = document.createDocumentFragment(), term = $('message-search').value.toLocaleLowerCase();
  for (const m of messages) {
   if (term && !m.Text.toLocaleLowerCase().includes(term) && !(m.SenderName || m.Sender).toLocaleLowerCase().includes(term)) continue;
   const article = element('article', '', 'message' + (m.IsSelf ? ' self' : ''));
   const avatar = element('div', Array.from(m.SenderName || m.Sender || '?')[0], 'avatar');
   const content = element('div', '', 'content'), date = new Date(m.Timestamp * 1000);
   const time = element('div', (m.SenderName || m.Sender) + '  ' + (Number.isNaN(date.valueOf()) ? m.Timestamp : date.toLocaleString()), 'timestamp');
   const bubble = element('div', '', 'bubble'), parts = m.Parts?.length ? m.Parts : legacyParts(m);
   const used = new Set();
   for (const part of parts) {
    const kind = part.Kind || 'unsupported', block = element('div', '', 'part kind-' + kind);
    const rich = /<[^>]+>|&lt;\/?\w+/.test(part.Text || '');
    if (kind === 'reply') block.append(element('blockquote', (part.ReferenceSender ? part.ReferenceSender + '\n' : '') +
     (part.ReferenceText || (part.ReferenceKind && part.ReferenceKind !== 'text' ? label('ReaderKind_' + part.ReferenceKind) : label('ReaderReferenceMissing')))));
    if (!['text','reply','system','markdown'].includes(kind)) {
     block.append(element('div', label('ReaderKind_' + kind), 'kind-title'));
     if (part.Title) block.append(element('strong', part.Title));
    }
    if (kind === 'markdown') block.append(element('div', label('ReaderKind_markdown'), 'kind-title'));
    if (part.Text && !rich) block.append(element('div', part.Text, 'message-text'));
    if (part.Detail) block.append(element('div', part.Detail, 'card-detail'));
    const candidates = (m.Media || []).filter(media => safeAsset(media.Path) && (
     part.MediaKeys?.includes(media.Key) || part.MediaKeys?.includes(media.Name) || parts.length === 1));
    for (const media of candidates) {
     if (used.has(media.Path)) continue;
     used.add(media.Path); block.append(renderMedia(media, kind));
    }
    if (['image','video','audio','sticker','file'].includes(kind) && !candidates.length) block.append(element('div',
     label(part.MediaState === 'unsupported-format' ? 'ReaderUnsupportedFormat' : 'ReaderFileMissing'), 'placeholder'));
    if (kind === 'unsupported' || kind === 'unparsed' || rich) block.append(element('div', label('ReaderSourceHint'), 'card-detail'));
    bubble.append(block);
   }
   for (const media of m.Media || []) {
    if (safeAsset(media.Path) && !used.has(media.Path)) { used.add(media.Path); bubble.append(renderMedia(media, media.Kind)); }
   }
   bubble.append(sourceDisclosure(m)); content.append(time, bubble); article.append(avatar, content); fragment.append(article);
  }
  $('messages').replaceChildren(fragment); $('messages').scrollTop = 0;
  $('messages').classList.remove('enter'); void $('messages').offsetWidth; $('messages').classList.add('enter');
 }
 $('contacts-search').oninput = list; $('message-search').oninput = render;
 $('previous').onclick = () => { if (page > 0) { page--; load(); } };
 $('next').onclick = () => { if (current && page + 1 < current.pages) { page++; load(); } };
 list(); if (index.conversations.length) { current = index.conversations[0]; list(); load(); }
})();
