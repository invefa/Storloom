"use strict";
(() => {
 const index = window.archiveIndex, labels = {...window.readerCatalogs?.[index.language], ...index.labels}, $ = id => document.getElementById(id);
 const archiveBase = window.archiveContentBase || '';
 let current = null, page = 0, messages = [], request = 0, pendingJump = null;
 const referenceMaps = new Map(), referenceLoads = new Map();
 const label = key => labels[key] || key;
 const contactNames = index.contactNames || {};
 const emojis=index.builtInEmoji || {};
 const resourceMap=new Map(Object.entries(index.resourceMedia||{}).map(([key,value])=>[key.toLowerCase(),value]));
 const mediaMatches=(keys,media)=>(keys||[]).some(key=>[media.Key,media.Name].some(value=>typeof value==='string'&&key.toLowerCase()===value.toLowerCase()));
 const readerPrefs=(()=>{try{return JSON.parse(localStorage.getItem('storloom.reader.view'))||{};}catch{return {};}})();
 let contextMode=false,returnPoints=[],restoreScroll=null,pinBottom=false;
 function savePrefs(){try{localStorage.setItem('storloom.reader.view',JSON.stringify(readerPrefs));}catch{}}
 for (const chat of index.conversations) chat.name = index.conversationNames?.[chat.id] || (chat.name==='0'?label('ReaderUnknownConversation'):chat.name);
 const element = (tag, text, className) => {
  const node = document.createElement(tag);
  if (text) node.textContent = text;
  if (className) node.className = className;
  return node;
 };
 const text = (id, value) => { $(id).textContent = value; };
 document.documentElement.lang = index.language;
 document.title = labels.ChatArchive; text('archive-title', labels.ChatArchive);
 $('contacts-search').placeholder = labels.ReaderSearch; $('message-search').placeholder = label('ReaderContentQuery');
 $('theme').title = labels.ReaderTheme; $('theme').setAttribute('aria-label', labels.ReaderTheme); text('theme', '◐');
 $('palette').title = label('ReaderPalette'); $('palette').setAttribute('aria-label', label('ReaderPalette'));
 for (const [value, name] of [['default','ReaderPaletteDefault'],['wechat','ReaderPaletteWeChat'],['qq','ReaderPaletteQQ']]) {
  const option = element('option', label(name)); option.value = value; $('palette').append(option);
 }
 function applyPalette(value) {
  const valid = ['default','wechat','qq'].includes(value) ? value : 'default';
  document.body.classList.toggle('palette-wechat', valid === 'wechat'); document.body.classList.toggle('palette-qq', valid === 'qq');
  $('palette').value = valid;
  layoutReferences();
 }
 try { applyPalette(localStorage.getItem('storloom.reader.palette')); } catch { applyPalette('default'); }
 $('palette').onchange = () => { applyPalette($('palette').value); try { localStorage.setItem('storloom.reader.palette', $('palette').value); } catch {} };
 text('previous', labels.ReaderPrevious); text('next', labels.ReaderNext);
 for(const [id,key] of [['content-label','ReaderContent'],['back-results','ReaderBackResults'],['page-bottom','ReaderPageBottom'],['auto-bottom-label','ReaderAutoBottom'],['auto-last-label','ReaderAutoLast']])text(id,label(key));
 $('sidebar-toggle').title=label('ReaderSidebar');$('sidebar-toggle').setAttribute('aria-label',label('ReaderSidebar'));
 $('auto-bottom').checked=!!readerPrefs.autoBottom;$('auto-last').checked=!!readerPrefs.autoLast;
 for(const [id,key] of [['first','ReaderFirst'],['last','ReaderLast'],['jump-label','ReaderJumpPage'],['page-go','ReaderGo'],['filter-label','ReaderFilters'],['kind-label','ReaderMessageType'],['sender-label','ReaderSender'],['from-label','ReaderDateFrom'],['to-label','ReaderDateTo'],['self-label','ReaderOnlySelf'],['reset-filter','ReaderResetFilters'],['find-page','ReaderFindNextPage'],['stop-search','ReaderStopSearch']])text(id,label(key));
 $('kind-filter').append(new Option(label('ReaderAllTypes'),''));
 for(const kind of ['text','image','sticker','video','audio','file','reply','forward','system','poke','reaction','link','miniapp','contact','location','call','transfer','redpacket','music','channel','gift','markdown','buttons','wallet','unsupported','unparsed'])$('kind-filter').append(new Option(label('ReaderKind_'+kind),kind));
 text('chat-title', labels.ReaderSelect);
 text('notice', labels.ReaderNotice + (index.resourceMedia && Object.keys(index.resourceMedia).length ? ' '+label('ReaderResourceRepairApplied') : index.unresolvedMedia ? ' ' + label('ReaderUnresolved') + ' ' + index.unresolvedMedia : '') +
  (index.unsupportedMessages ? ' | ' + label('ReaderUnsupportedCount') + ' ' + index.unsupportedMessages : ''));
 let dark = location.hash === '#dark' || (!location.hash && matchMedia('(prefers-color-scheme:dark)').matches);
 document.body.classList.toggle('dark', dark);
 $('theme').onclick = () => { dark = !dark; document.body.classList.toggle('dark', dark); };
 function list() {
  const term = $('contacts-search').value.toLocaleLowerCase(), fragment = document.createDocumentFragment();
  for (const chat of index.conversations) {
   if (!chat.name.toLocaleLowerCase().includes(term)) continue;
   const button = element('button'); button.title = chat.name; button.classList.toggle('active', current?.id === chat.id);
   button.append(element('strong', chat.name), element('small', chat.count + ' ' + labels.ReaderMessages));
   button.onclick = () => { stopSearch(); leaveContext();current = chat; page = readerPrefs.autoLast?Math.max(0,chat.pages-1):0; $('message-search').value = ''; list(); load(); };
   fragment.append(button);
  }
  $('conversations').replaceChildren(fragment);
 }
 function load() {
  if (!current) return;
  const id = ++request;
  text('chat-title', current.name); text('chat-meta', current.count + ' ' + labels.ReaderMessages);
  $('previous').disabled = page === 0; $('next').disabled = page + 1 >= current.pages;
  $('first').disabled=page===0;$('last').disabled=page+1>=current.pages;$('page-input').max=Math.max(1,current.pages);$('page-input').value=page+1;
  try{localStorage.setItem(positionKey,JSON.stringify({id:current.id,page}));}catch{}
  text('page', (page + 1) + ' / ' + current.pages);
  messages = []; $('messages').replaceChildren();
  const script = element('script'); script.src = archiveBase + 'conversations/' + current.id + '-' + page + '.js';
  script.onload = () => script.remove();
  script.onerror = () => { script.remove(); if (request === id) text('notice', labels.ReaderMissingMedia); };
  document.head.append(script);
 }
 window.receivePage = (conversation, p, items) => {
  const key=conversation+':'+p;if(pageLoads.has(key)){pageLoads.get(key)(items);pageLoads.delete(key);return;}
  if (current?.id !== conversation || p !== page) return;
  messages = items; render();
  if (pendingJump?.conversation === conversation && pendingJump.page === p) { highlight(pendingJump.id); pendingJump = null; }
  else if(restoreScroll!==null){$('messages').scrollTop=restoreScroll;restoreScroll=null;}
  else if(readerPrefs.autoBottom&&!contextMode)scrollBottom();
 };
 window.receiveReferences = (conversation, map) => {
  referenceMaps.set(conversation, map); referenceLoads.get(conversation)?.(map); referenceLoads.delete(conversation);
 };
 function highlight(id) {
  pinBottom=false;const target = $('msg-' + id); if (!target) {actionStatus(label('ReaderReferenceMissing'));return;}
  target.scrollIntoView({block:'center',behavior:matchMedia('(prefers-reduced-motion:reduce)').matches?'instant':'smooth'});target.classList.remove('referenced');void target.offsetWidth;target.classList.add('referenced');
  setTimeout(() => target.classList.remove('referenced'), 2600);
 }
 function scrollBottom(){pinBottom=true;$('messages').scrollTop=$('messages').scrollHeight;}
 function leaveContext(){contextMode=false;returnPoints=[];restoreScroll=null;pendingJump=null;$('back-results').hidden=true;}
 function showContext(id,targetPage=page){
  stopSearch();returnPoints.push({conversation:current.id,page,scroll:$('messages').scrollTop,context:contextMode,filters:readFilters()});
  contextMode=true;$('back-results').hidden=false;pinBottom=false;
  if(targetPage===page){render();highlight(id);}else{page=targetPage;pendingJump={conversation:current.id,page,id};load();}
 }
 function readFilters(){return Object.fromEntries(['message-search','kind-filter','sender-filter','date-from','date-to','self-filter'].map(id=>[id,$(id).type==='checkbox'?$(id).checked:$(id).value]));}
 $('back-results').onclick=()=>{stopSearch();const saved=returnPoints.pop();if(!saved)return;current=index.conversations.find(c=>c.id===saved.conversation);contextMode=saved.context;for(const [id,value] of Object.entries(saved.filters)){$(id).type==='checkbox'?$(id).checked=value:$(id).value=value;}$('back-results').hidden=returnPoints.length===0;restoreScroll=saved.scroll;pendingJump=null;if(page===saved.page){render();$('messages').scrollTop=restoreScroll;restoreScroll=null;}else{page=saved.page;list();load();}};
 $('page-bottom').onclick=scrollBottom;$('auto-bottom').onchange=()=>{readerPrefs.autoBottom=$('auto-bottom').checked;savePrefs();if(readerPrefs.autoBottom)scrollBottom();};
 $('auto-last').onchange=()=>{readerPrefs.autoLast=$('auto-last').checked;savePrefs();if(readerPrefs.autoLast&&current){leaveContext();go(current.pages-1);}};
 async function jump(reference, block) {
  const conversation = current.id;
  const local = messages.find(m => m.ServerId === reference || ('seq:' + m.Sequence) === reference);
  if (local) { showContext(local.Id); return; }
  let map = referenceMaps.get(conversation);
  if (!map) map = await new Promise(resolve => {
   if (referenceLoads.has(conversation)) { resolve(null); return; }
   referenceLoads.set(conversation, resolve);
   const script = element('script'); script.src = archiveBase + 'conversations/' + conversation + '-refs.js';
   script.onload = () => script.remove(); script.onerror = () => { script.remove(); referenceLoads.delete(conversation); resolve(null); };
   document.head.append(script);
  });
  if (current.id !== conversation) return;
  const target = map && Object.hasOwn(map, reference) ? map[reference] : null;
  if (!target) { actionStatus(label('ReaderReferenceMissing')); return; }
  showContext(target.id,target.page);
 }
 const safeAsset = path => /^assets\/[a-f0-9]{64}\.(png|jpg|jpeg|gif|webp|bmp|mp4|webm|mp3|wav|ogg|m4a|pdf|doc|docx|ppt|pptx|xls|xlsx|zip|7z|rar|txt|md|csv)$/.test(path);
 const assetIdentity=media=>(media.Kind||'image')+':'+(media.Path||'').replace(/\.[a-z0-9]+$/i,'');
 function layoutReferences() {
  for (const slot of document.querySelectorAll('.quote-slot')) {
   const parent = document.body.classList.contains('palette-wechat') ? slot.closest('.content').querySelector('.reference-zone') : slot;
   if (slot.quote && slot.quote.parentNode !== parent) parent.append(slot.quote);
  }
 }
 function senderPrefix(m) {
  if (m.Platform !== 'WeChat') return null;
  const prefix = /^(wxid_[A-Za-z0-9_-]+|[A-Za-z0-9_.-]+@openim):\r?\n/.exec(m.Text || '');
  return prefix && (m.Conversation.endsWith('@chatroom') || prefix[1] === m.Sender) ? prefix : null;
 }
 function visibleText(m, value) {
  const prefix = senderPrefix(m); return prefix && value.startsWith(prefix[0]) ? value.slice(prefix[0].length) : value;
 }
 function renderMedia(media, kind) {
  if(media.Kind==='file'){const link=element('a',label('ReaderSaveFile'),'file-download');link.href=archiveBase+media.Path;link.download=media.Name||'file';link.onclick=e=>{if(hostAction({action:'save-file',path:media.Path,name:media.Name}))e.preventDefault();};return link;}
  const node = element(media.Kind === 'video' ? 'video' : media.Kind === 'audio' ? 'audio' : 'img');
  node.src = archiveBase + media.Path;
  function fit(width, height) { if (width > 0 && height > 0) node.width = Math.max(1, Math.round(Math.min(width, 560, width * 420 / height)));if(pinBottom)requestAnimationFrame(scrollBottom); }
  if (node.tagName === 'IMG') { node.alt = media.Name; node.loading = 'lazy'; node.onload = () => fit(node.naturalWidth, node.naturalHeight); }
  else { node.controls = true; node.preload = 'none'; if (node.tagName === 'VIDEO') node.onloadedmetadata = () => fit(node.videoWidth,node.videoHeight); }
  node.onerror = () => node.replaceWith(element('div', label('ReaderKind_' + kind) + ' · ' + label('ReaderCannotPlay'), 'placeholder'));
  if(node.tagName==='IMG'){node.classList.add('previewable');node.tabIndex=0;node.setAttribute('role','button');node.title=label('ReaderPreviewImage');node.dataset.asset=media.Path;node.onclick=()=>openImage(media.Path,media.Name);node.onkeydown=e=>{if(e.key==='Enter'||e.key===' '){e.preventDefault();openImage(media.Path,media.Name);}};}
  return node;
 }
 function legacyParts(m) {
  const kind = m.Platform === 'WeChat' ? ({3:'image',34:'audio',43:'video',47:'sticker'}[m.Type % 4294967296]) : null;
  if (kind) return [{Kind:kind}];
  const structured = /<[^>]+>|^\s*[\[{]/.test(m.Text) || m.Text.length > 4000;
  return [{Kind:structured?'unsupported':'text',Text:structured?'':m.DisplayText || visibleText(m,m.Text)}];
 }
 const positionKey='storloom.reader.position:'+index.conversations.map(c=>c.id+':'+c.count).join('|');
 const pageLoads=new Map();let searching=0;
 function stopSearch(){searching++;for(const pending of pageLoads.values())pending.cancel?.();pageLoads.clear();$('stop-search').hidden=true;$('find-page').disabled=false;}
 const derivedParts=new WeakMap();
 function effectiveParts(m){if(!derivedParts.has(m))derivedParts.set(m,deriveParts(m));return derivedParts.get(m);}
 function deriveParts(m){
  const parts=m.Parts?.length?m.Parts:legacyParts(m);
  if(m.Platform!=='QQ'||!m.RawBase64||!parts.some(p=>p.Kind==='system'&&!p.Text||['unsupported','unparsed','reply'].includes(p.Kind)))return parts;
  try{
   for(const segment of protoFields(m.RawBase64)){
    const value=n=>segment.find(f=>f.id===n)?.value;
    const str=n=>typeof value(n)==='string'?value(n):'';
    if(str(48214).startsWith('<gtip')){
     const xml=str(48214);if(/<!DOCTYPE|<!ENTITY/i.test(xml))continue;
     const root=new DOMParser().parseFromString(xml,'application/xml');if(root.querySelector('parsererror'))continue;
     return [{Kind:'system',Text:Array.from(root.querySelectorAll('qq,nor')).map(e=>e.tagName==='qq'?contactNames[e.getAttribute('uin')]||e.getAttribute('nm')||e.getAttribute('jp')||e.getAttribute('uin')||e.textContent:e.getAttribute('txt')||e.textContent).join('')}];
    }
    if((value(45002)==='8'||m.Type===5)&&segment.some(f=>f.id===47702))return [{Kind:'reaction',ReferenceSender:str(47705),ReferenceText:str(47714)}];
    const poke=segment.find(f=>f.id===47401&&f.bytes);if(poke){const action=wireFields(poke.bytes).find(f=>f.id===47413);return [{Kind:'poke',Text:typeof action?.value==='string'?action.value:''}];}
   }
  }catch{}
  return parts;
 }
 function wireFields(data){let offset=0;const fields=[];function number(){let value=0n;for(let shift=0n;shift<70n&&offset<data.length;shift+=7n){const b=data[offset++];value|=BigInt(b&127)<<shift;if(!(b&128))return value;}throw Error('varint');}
  while(offset<data.length&&fields.length<10000){const tag=number(),id=Number(tag>>3n),wire=Number(tag&7n);if(!id)throw Error('tag');if(wire===0){fields.push({id,value:number().toString()});continue;}
   const size=wire===2?Number(number()):wire===1?8:wire===5?4:-1;if(size<0||offset+size>data.length)throw Error('size');const bytes=data.subarray(offset,offset+size);offset+=size;let value='';if(wire===2)try{value=new TextDecoder('utf-8',{fatal:true}).decode(bytes);}catch{}fields.push({id,bytes,value});}return fields;
 }
 function protoFields(encoded){if(encoded.length>1024*1024)return [];const data=Uint8Array.from(atob(encoded),c=>c.charCodeAt(0)),fields=wireFields(data),segments=fields.filter(f=>f.id===40800&&f.bytes);return (segments.length?segments.map(f=>wireFields(f.bytes)):[fields]).map(part=>{const extra=part.find(f=>f.id===48210&&f.bytes);if(extra)try{return [...part,...wireFields(extra.bytes)];}catch{}return part;});}
 function matches(m){
  if(contextMode)return true;
  const parts=effectiveParts(m),sender=contactNames[m.Conversation+'/'+m.Sender]||contactNames[m.Sender]||m.SenderName||m.Sender||'';
  const query=$('message-search').value.toLocaleLowerCase(),author=$('sender-filter').value.toLocaleLowerCase(),kind=$('kind-filter').value;
  const searchable=[m.DisplayText||m.Text||'',sender,...parts.flatMap(p=>[p.Text,p.Title,p.Detail,p.ReferenceText])].join(' ').toLocaleLowerCase();
  if(query&&!searchable.includes(query)||author&&!(sender+' '+m.Sender).toLocaleLowerCase().includes(author)||kind&&!parts.some(p=>p.Kind===kind)||$('self-filter').checked&&!m.IsSelf)return false;
  const from=$('date-from').value,to=$('date-to').value;if((from||to)&&!(m.Timestamp>0))return false;
  const date=new Date(m.Timestamp*1000);const localDate=[date.getFullYear(),String(date.getMonth()+1).padStart(2,'0'),String(date.getDate()).padStart(2,'0')].join('-');
  return (!from||localDate>=from)&&(!to||localDate<=to);
 }
 $('find-page').onclick=async()=>{
  if(!current)return;stopSearch();const generation=searching,conversation=current.id;const max=current.pages;$('find-page').disabled=true;$('stop-search').hidden=false;
  try{for(let n=page+1;n<max&&generation===searching;n++){
   text('filter-status',label('ReaderSearchingPage').replace('{0}',n+1).replace('{1}',max));
   const items=await new Promise((resolve,reject)=>{const key=conversation+':'+n,script=element('script');let timer=setTimeout(()=>{pageLoads.delete(key);script.remove();reject(Error('timeout'));},15000);const done=items=>{clearTimeout(timer);script.remove();resolve(items);};done.cancel=()=>done([]);pageLoads.set(key,done);script.onerror=()=>{clearTimeout(timer);pageLoads.delete(key);script.remove();reject(Error('page'));};script.src=archiveBase+'conversations/'+conversation+'-'+n+'.js';document.head.append(script);});
   if(generation!==searching)return;if(items.some(matches)){page=n;load();text('filter-status',label('ReaderMatchFound'));return;}
  }if(generation===searching)text('filter-status',label('ReaderNoMoreMatches'));}catch{if(generation===searching)text('filter-status',label('ReaderSearchFailed'));}
  finally{if(generation===searching){$('find-page').disabled=false;$('stop-search').hidden=true;}}
 };
 $('stop-search').onclick=()=>{stopSearch();text('filter-status','');};
 for(const id of ['kind-filter','sender-filter','date-from','date-to','self-filter'])$(id).oninput=()=>{stopSearch();leaveContext();render();};
 $('reset-filter').onclick=()=>{stopSearch();leaveContext();for(const id of ['message-search','kind-filter','sender-filter','date-from','date-to'])$(id).value='';$('self-filter').checked=false;text('filter-status','');render();};
 function emojiPath(platform,key){if(!key)return null;const name=String(key).trim().replace(/^\//,'').replace(/^\[(.*)\]$/,'$1');return emojis[platform+':'+key]||emojis[platform+':'+name]||emojis[platform+':/'+name]||emojis[platform+':['+name+']'];}
 const emojiPatterns={};
 for(const platform of ['QQ','WeChat']){const tokens=new Set();for(const key of Object.keys(emojis).filter(k=>k.startsWith(platform+':'))){let token=key.slice(platform.length+1);if(!token.startsWith('/')&&!token.startsWith('['))continue;const name=token.replace(/^\//,'').replace(/^\[(.*)\]$/,'$1');tokens.add('['+name+']');tokens.add('/'+name);}const escaped=Array.from(tokens).sort((a,b)=>b.length-a.length).map(s=>s.replace(/[.*+?^${}()|[\]\\]/g,'\\$&'));emojiPatterns[platform]=escaped.length?new RegExp(escaped.join('|'),'g'):null;}
 function inlineEmoji(platform,key,title){const path=emojiPath(platform,key);if(!path||!safeAsset(path))return element('span',title||'['+label('ReaderKind_sticker')+']','inline-face');const img=element('img','','inline-face');img.src=archiveBase+path;img.alt=title||key;img.title=title||key;img.onerror=()=>img.replaceWith(element('span',img.alt,'inline-face'));return img;}
 function textWithEmoji(node,value,platform){const pattern=emojiPatterns[platform];if(!pattern){node.textContent=value;return;}let at=0;for(const match of value.matchAll(pattern)){node.append(document.createTextNode(value.slice(at,match.index)));node.append(inlineEmoji(platform,match[0],match[0]));at=match.index+match[0].length;}node.append(document.createTextNode(value.slice(at)));}
 function sourceDisclosure(message) {
  const details = element('details', '', 'source'), summary = element('summary', label('ReaderSource'));
  details.append(summary);
  // Large source nodes are built only when explicitly requested.
  details.addEventListener('toggle', () => {
   if (!details.open || details.dataset.loaded) return;
   details.dataset.loaded = 'true'; const source = element('div', '', 'source-bubble'); details.append(source);
   const decodedText=(message.Text || '').startsWith('[unparsed message')?(message.Parts || []).filter(p=>['text','reply','system'].includes(p.Kind)).map(p=>p.Text || '').join(''):message.Text || '';
   if(decodedText) source.append(element('pre',decodedText));
   source.append(element('pre', JSON.stringify({platform:message.Platform,type:message.Type,sender:message.Sender,timestamp:message.Timestamp,serverId:message.ServerId,sequence:message.Sequence,media:message.Media},null,2)));
   if (message.Platform === 'QQ' && message.RawBase64) {
    try { source.append(element('h4',label('ReaderDecodedSource')),element('pre',describeProto(message.RawBase64))); }
    catch { source.append(element('small',label('ReaderCannotDecodeSource'))); }
   }
   if (message.SourceText) source.append(element('h4', label('ReaderDatabaseText')), element('pre', message.SourceText));
   for (const [name, value] of [['RawBase64',message.RawBase64],['ExtraRawBase64',message.ExtraRawBase64]]) {
    if (value) source.append(element('h4', name), element('pre', value));
   }
  });
  return details;
 }
 function describeProto(encoded) {
  const binary=atob(encoded), data=Uint8Array.from(binary.slice(0,262144),c=>c.charCodeAt(0));
  let fields=0;
  function decode(bytes,depth) {
   let offset=0; const lines=[];
   function number(){let value=0n;for(let shift=0n;shift<70n && offset<bytes.length;shift+=7n){const b=bytes[offset++];value|=BigInt(b&127)<<shift;if(!(b&128))return value;}throw Error('varint');}
   while(offset<bytes.length && ++fields<2000){const tag=number(), id=Number(tag>>3n), wire=Number(tag&7n);if(!id)throw Error('tag');
    if(wire===0){lines.push(id+': '+number());continue;}
    let size=wire===2?Number(number()):wire===1?8:wire===5?4:-1;if(size<0||offset+size>bytes.length)throw Error('size');
    const value=bytes.subarray(offset,offset+size);offset+=size;
    if(wire===2 && depth<6 && [40800,47423,47710,40900].includes(id)){try{lines.push(id+': {\n'+decode(value,depth+1).split('\n').map(l=>'  '+l).join('\n')+'\n}');continue;}catch{}}
    try{const text=new TextDecoder('utf-8',{fatal:true}).decode(value);if(!/[\u0000-\u0008\u000e-\u001f]/.test(text)){lines.push(id+': '+JSON.stringify(text));continue;}}catch{}
    lines.push(id+': ['+size+' bytes] '+Array.from(value.subarray(0,64),b=>b.toString(16).padStart(2,'0')).join('')+(size>64?'…':''));
   } return lines.join('\n');
  }
  const preview=decode(data,0);return preview+(binary.length>data.length?'\n'+label('ReaderSourceTruncated'):'');
 }
 function render() {
  pinBottom=false;const fragment = document.createDocumentFragment();let shownCount=0;
  text('filter-label',label(contextMode?'ReaderContextMode':'ReaderFilters'));
  const hasFilters=Object.values(readFilters()).some(value=>value!==''&&value!==false);
  for (const m of messages) {
   const repairedKeys=new Set(m.LocalMediaKeys||[]);
   function collectResourceKeys(parts){for(const p of parts||[]){for(const key of p.MediaKeys||[])repairedKeys.add(key);for(const entry of p.Forwarded||[])collectResourceKeys(entry.Parts);}}
   collectResourceKeys(m.Parts);
   const mediaEntries=[...(m.Media||[])];
   const repairedMessage=index.resourceMedia?.['message:'+m.Id];if(repairedMessage)mediaEntries.push(repairedMessage);
   for(const key of repairedKeys){const resource=resourceMap.get(key.toLowerCase());if(resource&&!mediaEntries.some(item=>item.Path===resource.Path&&item.Key===resource.Key))mediaEntries.push(resource);}
   const displayName=contactNames[m.Conversation+'/'+m.Sender] || contactNames[m.Sender] || m.SenderName || m.Sender;
   if(!matches(m))continue;shownCount++;
   const parts=effectiveParts(m),system=parts.every(p=>['system','poke','reaction'].includes(p.Kind));
   const face=p=>p.Inline||p.EmojiId!=null||p.Kind==='sticker'&&(/^#[0-9]+$/.test(p.Title||'')||/^\/[^\s]+$/.test(p.Title||'')||!!emojiPath(m.Platform,p.Title));
   const article = element('article', '', 'message' + (system ? ' system' : m.IsSelf ? ' self' : '')); if (m.Id) article.id = 'msg-' + m.Id;
   article.messageData=m;
   const avatar = element('div', displayName && displayName!=='0'?Array.from(displayName)[0]:'?', 'avatar');
   const content = element('div', '', 'content'), date = new Date(m.Timestamp * 1000);
   const sender=displayName && displayName!=='0'?displayName:label('ReaderUnknownSender');
   const time = element('div', (system ? '' : sender + '  ') + (m.Timestamp>0 && !Number.isNaN(date.valueOf()) ? date.toLocaleString() : label('ReaderUnknownTime')), 'timestamp');
   const bubble = element('div', '', 'bubble'), referenceZone = element('div', '', 'reference-zone');
   if(parts.length&&parts.every(p=>['image','video','sticker'].includes(p.Kind)&&!face(p))&&mediaEntries.some(media=>safeAsset(media.Path)))bubble.classList.add('visual-only');
   const used = new Set(), reserved = new Set();
   function reserve(items) { for (const p of items) { for (const key of p.MediaKeys || []) reserved.add(key.toLowerCase()); for (const entry of p.Forwarded || []) reserve(entry.Parts || []); } }
   reserve(parts);
   function partView(part, depth = 0) {
    const kind = part.Kind || 'unsupported', block = element('div', '', 'part kind-' + kind);
    if(face(part)){const inline=element('span','','part kind-face');inline.append(inlineEmoji(m.Platform,part.EmojiId??(part.Title||'').replace(/^#/,''),part.Title));return inline;}
    const matched = kind === 'forward' ? [] : mediaEntries.filter(media => safeAsset(media.Path) && (
     mediaMatches(part.MediaKeys,media) || (parts.length === 1 && depth === 0 && kind !== 'forward')));
    const candidates = matched.some(media => media.Quality !== 'thumbnail') ? matched.filter(media => media.Quality !== 'thumbnail') : matched;
    const visual=['image','video','sticker'].includes(kind)&&candidates.length>0;
    if(visual&&candidates.every(media=>used.has(assetIdentity(media))))return document.createDocumentFragment();
    const rich = /<[^>]+>|&lt;\/?\w+/.test(part.Text || '');
    if (kind === 'reply') {
     const quote = element('blockquote', '', 'quote');
     if (part.ReferenceSender) quote.append(element('span', (contactNames[m.Conversation+'/'+part.ReferenceSender] || contactNames[part.ReferenceSender] || part.ReferenceSender) + ': ', 'quote-sender'));
     quote.append(element('span', part.ReferenceText || (part.ReferenceKind && part.ReferenceKind !== 'text' ? label('ReaderKind_' + part.ReferenceKind) : label('ReaderReferenceMissing')), 'quote-text'));
     if (part.ReferenceId) { quote.tabIndex = 0; quote.setAttribute('role', 'button'); quote.title = label('ReaderJumpReference');
      quote.onclick = () => jump(part.ReferenceId, quote); quote.onkeydown = e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); jump(part.ReferenceId, quote); } }; }
     if (depth === 0) { const slot = element('div', '', 'quote-slot'); slot.quote = quote; slot.append(quote); block.append(slot); }
     else block.append(quote);
    }
    if (!['text','reply','system','markdown','poke','reaction'].includes(kind)&&!visual) {
     block.append(element('div', label('ReaderKind_' + kind), 'kind-title'));
     if (part.Title) block.append(element('strong', part.Title));
    }
    if (kind === 'markdown') block.append(element('div', label('ReaderKind_markdown'), 'kind-title'));
    if (part.Text && (!rich || ['text','system','markdown','poke'].includes(kind))) {const text=element('span','','message-text');textWithEmoji(text,depth===0?visibleText(m,part.Text):part.Text,m.Platform);block.append(text);}
    if(kind==='poke'&&!part.Text)block.append(element('span',label('ReaderKind_poke')));
    if(kind==='reaction'){block.classList.add('reaction-event');block.append(element('span',[part.ReferenceSender,label('ReaderReacted'),part.ReferenceText].filter(Boolean).join(' ')));}
    if (kind==='system' && !part.Text && !part.Title) block.append(element('span',label('ReaderEmptySystem'),'card-detail'));
    if (kind==='text' && !part.Text) block.append(element('span',label('ReaderEmptyMessage'),'card-detail'));
    if (part.Detail) block.append(element('div', part.Detail, 'card-detail'));
    if (kind === 'forward' && !part.Forwarded?.length) block.append(element('div', label('ReaderForwardMissing'), 'placeholder'));
    if (depth < 4 && part.Forwarded?.length) {
     function forward(entry) {
      const item = element('div', '', 'forwarded'); item.append(element('div', [entry.Sender,entry.Time].filter(Boolean).join(' · '), 'timestamp'));
      for (const child of entry.Parts || []) item.append(partView(child, depth + 1)); return item;
     }
     for (const entry of part.Forwarded.slice(0,5)) block.append(forward(entry));
     if (part.Forwarded.length > 5) {
      const rest = element('div', '', 'forward-rest'), more = element('button', '', 'forward-more'); rest.hidden = true;
      const expand = label('ReaderForwardExpand').replace('{0}', part.Forwarded.length - 5); more.textContent = expand; more.setAttribute('aria-expanded','false');
      let loaded = false;
      more.onclick = () => {
       if (!loaded) { for (const entry of part.Forwarded.slice(5)) rest.append(forward(entry)); loaded = true; }
       rest.hidden = !rest.hidden; more.textContent = rest.hidden ? expand : label('ReaderForwardCollapse'); more.setAttribute('aria-expanded',String(!rest.hidden));
      };
      block.append(rest, more);
     }
    }
    const shown = new Set();
    for (const media of candidates) {
     if (shown.has(assetIdentity(media)) || depth===0&&used.has(assetIdentity(media))) continue; shown.add(assetIdentity(media));
     used.add(assetIdentity(media)); block.append(renderMedia(media, kind));
     if (media.Quality === 'thumbnail') block.append(element('small', label('ReaderThumbnail')));
    }
    if (['image','video','audio','sticker','file'].includes(kind) && !candidates.length) block.append(element('div',
     label(part.MediaState === 'unsupported-format' ? 'ReaderUnsupportedFormat' : 'ReaderFileMissing'), 'placeholder'));
    if (kind === 'unsupported' || kind === 'unparsed' || rich) block.append(element('div', label('ReaderSourceHint'), 'card-detail'));
    return block;
   }
   for (const part of parts) bubble.append(partView(part));
   for (const media of mediaEntries) {
    if (safeAsset(media.Path) && !used.has(assetIdentity(media)) && !reserved.has((media.Key||'').toLowerCase()) && !reserved.has((media.Name||'').toLowerCase())) { used.add(assetIdentity(media)); bubble.append(renderMedia(media, media.Kind)); }
   }
   content.append(time, bubble, referenceZone, sourceDisclosure(m));
   if(hasFilters&&!contextMode){const context=element('button',label('ReaderShowContext'),'show-context');context.onclick=()=>showContext(m.Id);content.append(context);}
   if (!system) article.append(avatar); article.append(content); fragment.append(article);
  }
  $('messages').replaceChildren(fragment); layoutReferences(); $('messages').scrollTop = 0;
  if(!shownCount)$('messages').append(element('div',label('ReaderNoPageMatches'),'empty-results'));
  $('messages').classList.remove('enter'); void $('messages').offsetWidth; $('messages').classList.add('enter');
 }
 $('contacts-search').oninput = list; $('message-search').oninput=()=>{stopSearch();leaveContext();render();};
 function go(n){if(!current)return;stopSearch();page=Math.max(0,Math.min(current.pages-1,n));load();}
 $('previous').onclick=()=>go(page-1);$('next').onclick=()=>go(page+1);$('first').onclick=()=>go(0);$('last').onclick=()=>{if(current)go(current.pages-1);};
 $('page-go').onclick=()=>{if(!current)return;const n=Number($('page-input').value);if(Number.isInteger(n)&&n>=1&&n<=current.pages)go(n-1);else $('page-input').reportValidity();};$('page-input').onkeydown=e=>{if(e.key==='Enter')$('page-go').click();};
 const preview=$('image-preview'),picture=$('preview-image'),stage=$('image-stage');let imagePath='',imageName='',scale=1,x=0,y=0,drag=null;
 const split=$('sidebar-splitter');let sideDrag=null,windowDrag=null;
 function sideWidth(value){return Math.max(180,Math.min(480,innerWidth-320,Number(value)||290));}
 function applySidebar(){document.body.classList.toggle('sidebar-collapsed',!!readerPrefs.sidebarCollapsed);document.body.style.setProperty('--sidebar-width',sideWidth(readerPrefs.sidebarWidth)+'px');document.querySelector('aside').inert=!!readerPrefs.sidebarCollapsed;$('sidebar-toggle').setAttribute('aria-expanded',String(!readerPrefs.sidebarCollapsed));}
 $('sidebar-toggle').onclick=()=>{readerPrefs.sidebarCollapsed=!readerPrefs.sidebarCollapsed;applySidebar();savePrefs();};
 split.onpointerdown=e=>{if(e.button!==0)return;sideDrag={x:e.clientX,width:document.querySelector('aside').getBoundingClientRect().width};split.setPointerCapture(e.pointerId);e.preventDefault();};split.onpointermove=e=>{if(sideDrag){readerPrefs.sidebarWidth=sideWidth(sideDrag.width+e.clientX-sideDrag.x);applySidebar();}};split.onpointerup=()=>{sideDrag=null;savePrefs();};split.onpointercancel=()=>sideDrag=null;split.ondblclick=()=>{readerPrefs.sidebarWidth=290;applySidebar();savePrefs();};split.onkeydown=e=>{if(['ArrowLeft','ArrowRight'].includes(e.key)){e.preventDefault();readerPrefs.sidebarWidth=sideWidth((readerPrefs.sidebarWidth||290)+(e.key==='ArrowRight'?10:-10));applySidebar();savePrefs();}};applySidebar();
 const imageHeader=$('image-window-header');
 function centerPreview(){const width=Math.min(760,innerWidth-32),height=Math.min(560,innerHeight-40);Object.assign(preview.style,{width:width+'px',height:height+'px',left:Math.max(0,(innerWidth-width)/2)+'px',top:Math.max(0,(innerHeight-height)/2)+'px'});}
 imageHeader.onpointerdown=e=>{if(e.button!==0||e.target.closest('button'))return;const box=preview.getBoundingClientRect();windowDrag={x:e.clientX,y:e.clientY,left:box.left,top:box.top};imageHeader.setPointerCapture(e.pointerId);e.preventDefault();};
 imageHeader.onpointermove=e=>{if(!windowDrag)return;preview.style.left=Math.max(0,Math.min(innerWidth-preview.offsetWidth,windowDrag.left+e.clientX-windowDrag.x))+'px';preview.style.top=Math.max(0,Math.min(innerHeight-40,windowDrag.top+e.clientY-windowDrag.y))+'px';};imageHeader.onpointerup=()=>windowDrag=null;imageHeader.onpointercancel=()=>windowDrag=null;
 new ResizeObserver(()=>{if(preview.open)fitImage();}).observe(stage);
 for(const [id,key] of [['image-previous','ReaderPreviousImage'],['image-next','ReaderNextImage'],['image-close','ReaderClose'],['zoom-fit','ReaderFitImage'],['image-save','ReaderSaveImage']])text(id,label(key));
 preview.setAttribute('aria-label',label('ReaderPreviewImage'));$('zoom-in').title=label('ReaderZoomIn');$('zoom-out').title=label('ReaderZoomOut');
 function transform(){picture.style.transform=`translate(${x}px,${y}px) scale(${scale})`;text('zoom-value',Math.round(scale*100)+'%');}
 function fitImage(){x=y=0;scale=Math.min(1,(stage.clientWidth-32)/Math.max(1,picture.naturalWidth),(stage.clientHeight-32)/Math.max(1,picture.naturalHeight));transform();}
 function imageList(){const seen=new Set();return Array.from(document.querySelectorAll('#messages img.previewable')).filter(img=>{const key=assetIdentity({Kind:'image',Path:img.dataset.asset});if(seen.has(key))return false;seen.add(key);return true;});}
 function openImage(path,name){if(!safeAsset(path))return;imagePath=path;imageName=name||'image';picture.src=archiveBase+path;picture.alt=imageName;picture.onload=fitImage;if(!preview.open){centerPreview();preview.showModal();}const list=imageList(),at=list.findIndex(img=>img.dataset.asset===path);text('image-title',imageName+(at>=0?' · '+(at+1)+' / '+list.length:''));$('image-previous').disabled=at<=0;$('image-next').disabled=at<0||at+1>=list.length;}
 function changeImage(delta){const list=imageList(),at=list.findIndex(img=>img.dataset.asset===imagePath),next=list[at+delta];if(next)openImage(next.dataset.asset,next.alt);}
 function zoom(factor,cx=0,cy=0){const next=Math.max(.05,Math.min(20,scale*factor)),ratio=next/scale;x=cx-(cx-x)*ratio;y=cy-(cy-y)*ratio;scale=next;transform();}
 stage.addEventListener('wheel',e=>{e.preventDefault();const box=stage.getBoundingClientRect();zoom(Math.exp(-e.deltaY*.0015),e.clientX-box.left-box.width/2,e.clientY-box.top-box.height/2);},{passive:false});
 stage.onpointerdown=e=>{if(e.button!==0)return;drag={cx:e.clientX,cy:e.clientY,x,y};stage.setPointerCapture(e.pointerId);};stage.onpointermove=e=>{if(drag){x=drag.x+e.clientX-drag.cx;y=drag.y+e.clientY-drag.cy;transform();}};stage.onpointerup=()=>drag=null;stage.onpointercancel=()=>drag=null;
 $('zoom-in').onclick=()=>zoom(1.25);$('zoom-out').onclick=()=>zoom(.8);$('zoom-fit').onclick=fitImage;$('zoom-original').onclick=()=>{scale=1;x=y=0;transform();};picture.ondblclick=()=>{if(scale===1)fitImage();else{scale=1;x=y=0;transform();}};
 $('image-previous').onclick=()=>changeImage(-1);$('image-next').onclick=()=>changeImage(1);$('image-close').onclick=()=>preview.close();preview.onclick=e=>{if(e.target===preview)preview.close();};preview.onkeydown=e=>{if(e.key==='ArrowLeft')changeImage(-1);if(e.key==='ArrowRight')changeImage(1);if(e.key==='+')zoom(1.25);if(e.key==='-')zoom(.8);};window.addEventListener('resize',()=>{if(preview.open)fitImage();});
 function hostAction(message){if(window.chrome?.webview){window.chrome.webview.postMessage(message);return true;}return false;}
 let statusTimer;function actionStatus(value){text('action-status',value);clearTimeout(statusTimer);statusTimer=setTimeout(()=>text('action-status',''),4000);}
 window.chrome?.webview?.addEventListener('message',e=>{if(e.data?.action==='reader-status'&&typeof e.data.text==='string')actionStatus(e.data.text);});
 function saveImage(path,name){if(!safeAsset(path))return;if(hostAction({action:'save-image',path,name}))return;const a=element('a');a.href=archiveBase+path;a.download=(name||'image').replace(/[\\/:*?"<>|]/g,'_');document.body.append(a);a.click();a.remove();}
 $('image-save').onclick=()=>saveImage(imagePath,imageName);
 async function copyText(value){if(hostAction({action:'copy-text',text:value}))return;try{await navigator.clipboard.writeText(value);actionStatus(label('ReaderCopied'));}catch{actionStatus(label('ReaderCopyFailed'));}}
 const menu=$('reader-menu');function hideMenu(){menu.hidden=true;}
 function menuItem(title,action){const button=element('button',label(title));button.setAttribute('role','menuitem');button.onclick=()=>{hideMenu();action();};menu.append(button);}
 document.addEventListener('contextmenu',e=>{
  const image=e.target.closest('img.previewable,#preview-image'),article=e.target.closest('.message');if(!image&&!article)return;e.preventDefault();menu.replaceChildren();
  (preview.open?preview:document.body).append(menu);
  if(image){const path=image.id==='preview-image'?imagePath:image.dataset.asset,name=image.alt;menuItem('ReaderPreviewImage',()=>openImage(path,name));menuItem('ReaderSaveImage',()=>saveImage(path,name));}
  if(article){const m=article.messageData;menuItem('ReaderCopyMessage',()=>copyText(effectiveParts(m).map(p=>p.Text||p.Title||p.ReferenceText||'').filter(Boolean).join('')));if(m.Id)menuItem('ReaderCopyMessageId',()=>copyText(m.ServerId||m.Id));menuItem('ReaderSource',()=>{article.querySelector('details.source').open=true;});}
  menu.hidden=false;menu.style.left=Math.min(e.clientX,innerWidth-menu.offsetWidth-8)+'px';menu.style.top=Math.min(e.clientY,innerHeight-menu.offsetHeight-8)+'px';menu.querySelector('button')?.focus();
 });
 document.addEventListener('pointerdown',e=>{if(!menu.contains(e.target))hideMenu();});document.addEventListener('keydown',e=>{if(e.key==='Escape')hideMenu();if(!menu.hidden&&['ArrowDown','ArrowUp'].includes(e.key)){e.preventDefault();const buttons=Array.from(menu.querySelectorAll('button')),at=buttons.indexOf(document.activeElement);buttons[(at+(e.key==='ArrowDown'?1:-1)+buttons.length)%buttons.length].focus();}});$('messages').addEventListener('scroll',hideMenu);
 $('messages').addEventListener('wheel',()=>pinBottom=false,{passive:true});$('messages').addEventListener('pointerdown',()=>pinBottom=false);
 document.addEventListener('keydown',e=>{if((e.ctrlKey||e.metaKey)&&e.key==='f'){e.preventDefault();$('message-search').focus();}if(e.altKey&&e.key==='ArrowLeft'){e.preventDefault();go(page-1);}if(e.altKey&&e.key==='ArrowRight'){e.preventDefault();go(page+1);}});
 list(); if (index.conversations.length) { current=index.conversations[0];try{const saved=JSON.parse(localStorage.getItem(positionKey));const found=index.conversations.find(c=>c.id===saved?.id);if(found){current=found;page=Math.max(0,Math.min(current.pages-1,Number(saved.page)||0));}}catch{}if(readerPrefs.autoLast)page=Math.max(0,current.pages-1);list();load(); }
})();
