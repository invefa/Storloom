"use strict";
(() => {
 const index = window.archiveIndex, labels = {...window.readerCatalogs?.[index.language], ...index.labels}, $ = id => document.getElementById(id);
 const archiveBase = window.archiveContentBase || '';
 let current = null, page = 0, messages = [], request = 0, pendingJump = null;
 const referenceMaps = new Map(), referenceLoads = new Map();
 const label = key => labels[key] || key;
 const contactNames = index.contactNames || {};
 const baseline=index.identityBaseline||{contactNames,avatars:index.avatars||{}};
 const identityHistory=new Map();
 for(const profile of Object.values(index.identityVersions||{})){const list=identityHistory.get(profile.Sender)||[];list.push(profile);identityHistory.set(profile.Sender,list);}
 function identityOf(m){return m.Identity||index.identityVersions?.[m.IdentityVersion]||{Sender:m.Sender,Conversation:m.Conversation,RecordedName:(m.SenderName&&m.SenderName!==m.Sender?m.SenderName:null)||baseline.contactNames?.[m.Conversation+'/'+m.Sender]||baseline.contactNames?.[m.Sender]||m.SenderName,Avatar:baseline.avatars?.[m.Conversation+'/'+m.Sender]||baseline.avatars?.[m.Sender],ObservedAt:baseline.observedAt};}
 function nameOf(m){const p=identityOf(m);return p.GroupCard||p.FriendRemark||p.Nickname||p.RecordedName||m.Sender;}
 function identityDetails(m){
  const current=identityOf(m),details=element('details','','identity-details'),summary=element('summary',nameOf(m)||label('ReaderUnknownSender'));
  details.append(summary);const panel=element('div','','identity-panel');details.append(panel);
  panel.append(element('strong',label('ReaderIdentity')));
  if(current.Avatar&&safeAsset(current.Avatar)){const portrait=element('img','','identity-portrait');portrait.src=archiveBase+current.Avatar;portrait.alt=nameOf(m);portrait.tabIndex=0;portrait.setAttribute('role','button');portrait.title=label('ReaderPreviewImage');portrait.onclick=()=>openImage(current.Avatar,nameOf(m));portrait.onkeydown=e=>{if(e.key==='Enter'||e.key===' '){e.preventDefault();portrait.click();}};panel.append(portrait);}

  const rows=[['ReaderPlatform',m.Platform],['ReaderConversation',m.ConversationName||m.Conversation],['ReaderAccount',m.Sender],['ReaderNickname',current.Nickname],['ReaderFriendRemark',current.FriendRemark],['ReaderGroupCard',current.GroupCard]];
  if(current.RecordedName)rows.push(['ReaderRecordedName',current.RecordedName]);
  const table=element('dl');for(const [name,value] of rows)table.append(element('dt',label(name)),element('dd',value||label('ReaderNotRecorded')));panel.append(table);
  panel.append(element('small',label('ReaderIdentityProvenance')));
  const versions=(identityHistory.get(m.Sender)||[]).filter(p=>!p.Conversation||p.Conversation===m.Conversation);
  if(versions.length){panel.append(element('strong',label('ReaderIdentityHistory')));const list=element('div','','identity-history');panel.append(list);
   for(const version of versions){const item=element('div','','identity-version');
    if(version.Avatar&&safeAsset(version.Avatar)){const img=element('img');img.src=archiveBase+version.Avatar;img.alt=label('ReaderPreviewImage');img.tabIndex=0;img.setAttribute('role','button');img.onclick=()=>openImage(version.Avatar,nameOf(m));img.onkeydown=e=>{if(e.key==='Enter'||e.key===' '){e.preventDefault();img.click();}};item.append(img);}
    const values=[['ReaderNickname',version.Nickname],['ReaderFriendRemark',version.FriendRemark],['ReaderGroupCard',version.GroupCard],['ReaderRecordedName',version.RecordedName]].filter(([,v])=>v).map(([k,v])=>label(k)+': '+v);
    item.append(element('span',values.join(' · ')||label('ReaderNotRecorded')));
    if(version.ObservedAt)item.append(element('small',label('ReaderObservedAt')+': '+new Date(version.ObservedAt).toLocaleString()));
    list.append(item);
   }
  }
  return details;
 }
 const emojis=index.builtInEmoji || {};
 const resourceMap=new Map(Object.entries(index.resourceMedia||{}).map(([key,value])=>[key.toLowerCase(),value]));
 const mediaMatches=(keys,media)=>(keys||[]).some(key=>[media.Key,media.Name].some(value=>typeof value==='string'&&key.toLowerCase()===value.toLowerCase()));
 const readerPrefs=(()=>{try{return JSON.parse(localStorage.getItem('storloom.reader.view'))||{};}catch{return {};}})();
 let contextMode=false,returnPoints=[],restoreScroll=null,pinBottom=false,archiveResults=null,archiveSearch=null;
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
 for(const kind of ['text','image','sticker','video','audio','file','reply','forward','system','poke','recall','reaction','link','miniapp','contact','location','call','transfer','redpacket','music','channel','gift','markdown','buttons','wallet','unsupported','unparsed'])$('kind-filter').append(new Option(label('ReaderKind_'+kind),kind));
 for(const [id,key] of [['scope-label','ReaderSearchScope'],['image-locate','ReaderLocateImage'],['image-copy','ReaderCopyImage']])text(id,label(key));
 $('search-scope').options[0].textContent=label('ReaderScopeConversation');$('search-scope').options[1].textContent=label('ReaderScopeArchive');
 function toggleFilters(open=!$('filter-panel').open){$('filter-panel').open=open;$('filter-toggle').setAttribute('aria-expanded',String(open));}
 $('filter-toggle').onclick=()=>toggleFilters();
 function scopeHint(){text('search-scope-note',label($('search-scope').value==='archive'?'ReaderArchiveSearchHint':'ReaderConversationSearchHint'));}
 scopeHint();
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
   const avatarPath=index.conversationAvatars?.[chat.id];if(avatarPath&&safeAsset(avatarPath)){button.classList.add('has-avatar');const img=element('img');img.src=archiveBase+avatarPath;img.alt='';img.onerror=()=>{img.remove();button.classList.remove('has-avatar');};button.append(img);}
   button.append(element('strong', chat.name), element('small', chat.count + ' ' + labels.ReaderMessages));
   button.onclick = () => { stopSearch();archiveResults=null;archiveSearch=null; leaveContext();current = chat; page = readerPrefs.autoLast?Math.max(0,chat.pages-1):0; $('message-search').value = ''; list(); load(); };
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
  if (pendingJump?.conversation === conversation && pendingJump.page === p) { highlight(pendingJump.id,false); pendingJump = null; }
  else if(restoreScroll!==null){$('messages').scrollTop=restoreScroll;restoreScroll=null;}
  else if(readerPrefs.autoBottom&&!contextMode)scrollBottom();
 };
 window.receiveReferences = (conversation, map) => {
  referenceMaps.set(conversation, map); referenceLoads.get(conversation)?.(map); referenceLoads.delete(conversation);
 };
 function highlight(id,animate=true) {
  pinBottom=false;const target = $('msg-' + id); if (!target) {actionStatus(label('ReaderReferenceMissing'));return;}
  target.scrollIntoView({block:'center',behavior:animate&&!matchMedia('(prefers-reduced-motion:reduce)').matches?'smooth':'instant'});target.classList.remove('referenced');void target.offsetWidth;target.classList.add('referenced');
  setTimeout(() => target.classList.remove('referenced'), 2600);
 }
 function scrollBottom(){pinBottom=true;$('messages').scrollTop=$('messages').scrollHeight;}
 function leaveContext(){contextMode=false;returnPoints=[];restoreScroll=null;pendingJump=null;$('back-results').hidden=true;}
 function showContext(id,targetPage=page,origin=null){
  const pane=$('messages'),scroll=pane.scrollTop;
  const anchor=origin?.closest('.message')||Array.from(pane.children).find(node=>node.getBoundingClientRect().bottom>pane.getBoundingClientRect().top);
  const anchorId=anchor?.id,anchorTop=anchor?.getBoundingClientRect().top;
  const needsRender=!contextMode&&Object.values(readFilters()).some(value=>value!==''&&value!==false);
  stopSearch();returnPoints.push({conversation:current.id,page,scroll:$('messages').scrollTop,context:contextMode,filters:readFilters(),results:archiveResults,session:archiveSearch,items:messages});
  toggleFilters(false);contextMode=true;archiveResults=null;$('back-results').hidden=false;pinBottom=false;
  if(targetPage===page){
   if(needsRender){render();pane.scrollTop=scroll;}
   const restored=anchorId&&$(anchorId);if(restored)pane.scrollTop+=restored.getBoundingClientRect().top-anchorTop;
   highlight(id);
  }else{page=targetPage;pendingJump={conversation:current.id,page,id};load();}
 }
 function readFilters(){return Object.fromEntries(['message-search','kind-filter','sender-filter','date-from','date-to','self-filter'].map(id=>[id,$(id).type==='checkbox'?$(id).checked:$(id).value]));}
 $('back-results').onclick=()=>{stopSearch();const saved=returnPoints.pop();if(!saved)return;current=index.conversations.find(c=>c.id===saved.conversation);contextMode=saved.context;for(const [id,value] of Object.entries(saved.filters)){$(id).type==='checkbox'?$(id).checked=value:$(id).value=value;}$('back-results').hidden=returnPoints.length===0;page=saved.page;messages=saved.items;archiveResults=saved.results;archiveSearch=saved.session;pendingJump=null;list();updateHeading();render();if(archiveResults)resultPager();else normalPager();$('messages').scrollTop=saved.scroll;};
 function updateHeading(){text('chat-title',current?.name||labels.ReaderSelect);text('chat-meta',current?current.count+' '+labels.ReaderMessages:'');}
 function resultContext(row,origin){
  const saved={conversation:current.id,page,scroll:$('messages').scrollTop,context:contextMode,filters:readFilters(),results:archiveResults,session:archiveSearch,items:messages};
  stopSearch();returnPoints.push(saved);toggleFilters(false);contextMode=true;archiveResults=null;$('back-results').hidden=false;current=index.conversations.find(c=>c.id===row.conversation);page=row.page;pendingJump={conversation:current.id,page,id:row.message.Id};toggleFilters(false);list();load();
 }
 $('page-bottom').onclick=scrollBottom;$('auto-bottom').onchange=()=>{readerPrefs.autoBottom=$('auto-bottom').checked;savePrefs();if(readerPrefs.autoBottom)scrollBottom();};
 $('auto-last').onchange=()=>{readerPrefs.autoLast=$('auto-last').checked;savePrefs();if(readerPrefs.autoLast&&current){leaveContext();go(current.pages-1);}};
 async function jump(reference, block) {
  const sourceRow=block?.closest('.message')?.resultRow;const conversation = sourceRow?.conversation||current.id;
  const local = (sourceRow?[sourceRow.message]:messages).find(m => m.ServerId === reference || ('seq:' + m.Sequence) === reference);
  if (local) { if(sourceRow)resultContext({...sourceRow,message:local},block);else showContext(local.Id,page,block); return; }
  let map = referenceMaps.get(conversation);
  if (!map) map = await new Promise(resolve => {
   if (referenceLoads.has(conversation)) { resolve(null); return; }
   referenceLoads.set(conversation, resolve);
   const script = element('script'); script.src = archiveBase + 'conversations/' + conversation + '-refs.js';
   script.onload = () => script.remove(); script.onerror = () => { script.remove(); referenceLoads.delete(conversation); resolve(null); };
   document.head.append(script);
  });
  if (!sourceRow&&current.id !== conversation) return;
  const target = map && Object.hasOwn(map, reference) ? map[reference] : null;
  if (!target) { actionStatus(label('ReaderReferenceMissing')); return; }
  if(sourceRow)resultContext({conversation,page:target.page,message:{Id:target.id}},block);else showContext(target.id,target.page);
 }
 const safeAsset = path => /^assets\/[a-f0-9]{64}\.(png|jpg|jpeg|gif|webp|bmp|mp4|webm|mp3|wav|ogg|m4a|pdf|doc|docx|ppt|pptx|xls|xlsx|zip|7z|rar|txt|md|csv)$/.test(path);
 const logicalImage=media=>{for(const name of [media.Key,media.Name]){const hash=/(?:^|[^a-f0-9])([a-f0-9]{32})(?![a-f0-9])/i.exec(name||'');if(hash)return 'md5:'+hash[1].toLowerCase();}return (media.Path||'').replace(/\.[a-z0-9]+$/i,'');};
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
  if(node.tagName==='IMG'){node.classList.add('previewable');node.tabIndex=0;node.setAttribute('role','button');node.title=label('ReaderPreviewImage');node.dataset.asset=media.Path;node.onclick=()=>openImage(media.Path,media.Name,node);node.onkeydown=e=>{if(e.key==='Enter'||e.key===' '){e.preventDefault();openImage(media.Path,media.Name,node);}};}
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
  const parts=index.messageParts?.[m.Id]||(m.Parts?.length?m.Parts:legacyParts(m));
  if(m.Platform!=='QQ'||!m.RawBase64||!parts.some(p=>p.Kind==='system'&&!p.Text||['unsupported','unparsed','reply','reaction','recall'].includes(p.Kind)))return parts;
  try{
   for(const segment of protoFields(m.RawBase64)){
    const value=n=>segment.find(f=>f.id===n)?.value;
    const str=n=>typeof value(n)==='string'?value(n):'';
    if(str(48214).startsWith('<gtip')){
     const xml=str(48214);if(/<!DOCTYPE|<!ENTITY/i.test(xml))continue;
     const root=new DOMParser().parseFromString(xml,'application/xml');if(root.querySelector('parsererror'))continue;
     return [{Kind:'system',Text:Array.from(root.querySelectorAll('qq,nor')).map(e=>e.tagName==='qq'?contactNames[e.getAttribute('uin')]||e.getAttribute('nm')||e.getAttribute('jp')||e.getAttribute('uin')||e.textContent:e.getAttribute('txt')||e.textContent).join('')}];
    }
    if((value(45002)==='8'||m.Type===5)&&segment.some(f=>f.id===47702))return [{Kind:'recall',ReferenceSender:str(47716)||str(47715)||str(47714),ReferenceText:str(47705),Detail:str(47713),RecallRole:Number(value(47702))}];
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
 function matches(m,force=false){
  if(contextMode&&!force)return true;
  const parts=effectiveParts(m),sender=nameOf(m)||'';
  const query=$('message-search').value.toLocaleLowerCase(),author=$('sender-filter').value.toLocaleLowerCase(),kind=$('kind-filter').value;
  const searchable=[m.DisplayText||m.Text||'',sender,...parts.flatMap(p=>[p.Text,p.Title,p.Detail,p.ReferenceText])].join(' ').toLocaleLowerCase();
  if(query&&!searchable.includes(query)||author&&!([sender,m.Sender,...Object.values(identityOf(m)).filter(v=>typeof v==='string')].join(' ')).toLocaleLowerCase().includes(author)||kind&&!parts.some(p=>p.Kind===kind)||$('self-filter').checked&&!m.IsSelf)return false;
  const from=$('date-from').value,to=$('date-to').value;if((from||to)&&!(m.Timestamp>0))return false;
  const date=new Date(m.Timestamp*1000);const localDate=[date.getFullYear(),String(date.getMonth()+1).padStart(2,'0'),String(date.getDate()).padStart(2,'0')].join('-');
  return (!from||localDate>=from)&&(!to||localDate<=to);
 }
 function readPage(conversation,n){return new Promise((resolve,reject)=>{const key=conversation+':'+n,script=element('script');const done=items=>{clearTimeout(timer);pageLoads.delete(key);script.remove();resolve(items);};const timer=setTimeout(()=>{pageLoads.delete(key);script.remove();reject(Error('timeout'));},15000);done.cancel=()=>done([]);pageLoads.set(key,done);script.onerror=()=>{clearTimeout(timer);pageLoads.delete(key);script.remove();reject(Error('page'));};script.src=archiveBase+'conversations/'+conversation+'-'+n+'.js';document.head.append(script);});}
 function resultPager(){const n=archiveSearch.batch;for(const id of ['first','previous'])$(id).disabled=n===0;$('next').disabled=archiveSearch.finished;$('last').disabled=archiveSearch.endBatch==null||n===archiveSearch.endBatch;$('page-input').max=archiveSearch.starts.length;$('page-input').value=n+1;text('page',label('ReaderResultBatch').replace('{0}',n+1));text('chat-title',label('ReaderScopeArchive'));text('chat-meta',archiveResults.length+' '+label('ReaderMatches'));}
 async function searchArchive(batch){
  stopSearch();leaveContext();const generation=searching;
  const fingerprint=JSON.stringify(readFilters());
  if(!archiveSearch||archiveSearch.fingerprint!==fingerprint)archiveSearch={fingerprint,starts:[{c:0,p:0,i:0}],batch:0,finished:false,origin:{conversation:current.id,page,items:messages}};
  const session=archiveSearch;if(batch===undefined)batch=session.batch+1;
  if(!session.starts[batch])return;
  const cursor={...session.starts[batch]},rows=[];const total=index.conversations.reduce((n,c)=>n+c.pages,0);let scanned=index.conversations.slice(0,cursor.c).reduce((n,c)=>n+c.pages,0)+cursor.p;
  $('find-page').disabled=true;$('stop-search').hidden=false;
  try{
   outer:while(cursor.c<index.conversations.length){
    const chat=index.conversations[cursor.c];if(cursor.p>=chat.pages){cursor.c++;cursor.p=0;cursor.i=0;continue;}
    text('filter-status',label('ReaderSearchingPage').replace('{0}',scanned+1).replace('{1}',total));
    const items=await readPage(chat.id,cursor.p);if(generation!==searching)return;
    for(;cursor.i<items.length;cursor.i++){const message=items[cursor.i];if(matches(message,true)){rows.push({message,conversation:chat.id,page:cursor.p});if(rows.length>=100){cursor.i++;if(cursor.i>=items.length){cursor.i=0;cursor.p++;if(cursor.p>=chat.pages){cursor.p=0;cursor.c++;}}break outer;}}}
    cursor.p++;cursor.i=0;scanned++;await new Promise(resolve=>setTimeout(resolve,0));if(generation!==searching)return;
   }
   session.batch=batch;session.finished=cursor.c>=index.conversations.length;if(session.finished)session.endBatch=batch;if(!session.finished)session.starts[batch+1]={...cursor};else session.starts.length=batch+1;
   archiveResults=rows;render();resultPager();text('filter-status',rows.length?label('ReaderMatchFound'):label('ReaderNoMoreMatches'));toggleFilters(false);
  }catch{if(generation===searching)text('filter-status',label('ReaderSearchFailed'));}
  finally{if(generation===searching){$('find-page').disabled=false;$('stop-search').hidden=true;}}
 }
 $('find-page').onclick=async()=>{
  if(!current)return;if($('search-scope').value==='archive'){await searchArchive(archiveSearch&&archiveResults?undefined:0);return;}
  stopSearch();const generation=searching,conversation=current.id,max=current.pages;$('find-page').disabled=true;$('stop-search').hidden=false;
  try{for(let n=page+1;n<max&&generation===searching;n++){
   text('filter-status',label('ReaderSearchingPage').replace('{0}',n+1).replace('{1}',max));const items=await readPage(conversation,n);
   if(generation!==searching)return;if(items.some(m=>matches(m,true))){page=n;load();text('filter-status',label('ReaderMatchFound'));return;}
  }if(generation===searching)text('filter-status',label('ReaderNoMoreMatches'));}catch{if(generation===searching)text('filter-status',label('ReaderSearchFailed'));}
  finally{if(generation===searching){$('find-page').disabled=false;$('stop-search').hidden=true;}}
 };
 $('stop-search').onclick=()=>{stopSearch();text('filter-status',label('ReaderSearchStopped'));};
 function discardResults(){if(archiveSearch?.origin){const origin=archiveSearch.origin;current=index.conversations.find(c=>c.id===origin.conversation);page=origin.page;messages=origin.items;}archiveResults=null;archiveSearch=null;}
 function filterChanged(){stopSearch();discardResults();leaveContext();text('filter-status','');updateHeading();render();normalPager();}
 function normalPager(){if(!current)return;$('previous').disabled=$('first').disabled=page===0;$('next').disabled=$('last').disabled=page+1>=current.pages;$('page-input').max=current.pages;$('page-input').value=page+1;text('page',(page+1)+' / '+current.pages);}
 for(const id of ['message-search','kind-filter','sender-filter','date-from','date-to','self-filter'])$(id).oninput=filterChanged;
 $('search-scope').onchange=()=>{filterChanged();scopeHint();};
 for(const id of ['message-search','sender-filter'])$(id).onkeydown=e=>{if(e.key==='Enter')$('find-page').click();};
 $('reset-filter').onclick=()=>{for(const id of ['message-search','kind-filter','sender-filter','date-from','date-to'])$(id).value='';$('self-filter').checked=false;filterChanged();};
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
  text('filter-label',label('ReaderFilters'));$('filter-toggle').classList.toggle('active',Object.values(readFilters()).some(v=>v!==''&&v!==false));
  const hasFilters=Object.values(readFilters()).some(value=>value!==''&&value!==false);
  for (const row of archiveResults||messages.map(message=>({message,conversation:current.id,page}))) {
   const m=row.message;
   const repairedKeys=new Set(m.LocalMediaKeys||[]);
   function collectResourceKeys(parts){for(const p of parts||[]){for(const key of p.MediaKeys||[])repairedKeys.add(key);for(const entry of p.Forwarded||[])collectResourceKeys(entry.Parts);}}
   collectResourceKeys(m.Parts);
   const mediaEntries=[...(m.Media||[])];
   const repairedMessage=index.resourceMedia?.['message:'+m.Id];if(repairedMessage)mediaEntries.push(repairedMessage);
   for(const key of repairedKeys){const resource=resourceMap.get(key.toLowerCase());if(resource&&!mediaEntries.some(item=>item.Path===resource.Path&&item.Key===resource.Key))mediaEntries.push(resource);}
   const displayName=nameOf(m);
   if(!archiveResults&&$('search-scope').value!=='archive'&&!matches(m))continue;shownCount++;
   const parts=effectiveParts(m),system=parts.every(p=>['system','poke','reaction','recall'].includes(p.Kind));
   const face=p=>p.Inline||p.EmojiId!=null||p.Kind==='sticker'&&(/^#[0-9]+$/.test(p.Title||'')||/^\/[^\s]+$/.test(p.Title||'')||!!emojiPath(m.Platform,p.Title));
   const article = element('article', '', 'message' + (system ? ' system' : m.IsSelf ? ' self' : '')); if (m.Id) article.id = 'msg-' + m.Id;
   article.messageData=m;article.resultRow=archiveResults?row:null;
   const avatar = element('div', displayName && displayName!=='0'?Array.from(displayName)[0]:'?', 'avatar');
   const avatarPath=identityOf(m).Avatar;if(avatarPath&&safeAsset(avatarPath)){const img=element('img');img.src=archiveBase+avatarPath;img.alt=displayName;img.onerror=()=>img.remove();avatar.append(img);}
   const content = element('div', '', 'content'), date = new Date(m.Timestamp * 1000);
   const sender=displayName && displayName!=='0'?displayName:label('ReaderUnknownSender');
   const time = element('div', (system ? '' : sender + '  ') + (m.Timestamp>0 && !Number.isNaN(date.valueOf()) ? date.toLocaleString() : label('ReaderUnknownTime')), 'timestamp');
   if(!system){const info=identityDetails(m);time.replaceChildren(info,document.createTextNode('  '+(m.Timestamp>0&&!Number.isNaN(date.valueOf())?date.toLocaleString():label('ReaderUnknownTime'))));avatar.tabIndex=0;avatar.setAttribute('role','button');avatar.title=label('ReaderIdentity');avatar.dataset.profile='true';avatar.onclick=()=>{for(const other of document.querySelectorAll('.identity-details[open]'))if(other!==info)other.open=false;info.open=!info.open;};avatar.onkeydown=e=>{if(e.key==='Enter'||e.key===' '){e.preventDefault();avatar.click();}};}

   const bubble = element('div', '', 'bubble'), referenceZone = element('div', '', 'reference-zone');
   if(parts.length&&parts.every(p=>['image','video','sticker'].includes(p.Kind)&&!face(p))&&mediaEntries.some(media=>safeAsset(media.Path)))bubble.classList.add('visual-only');
   if(parts.length&&parts.every(p=>p.Kind==='audio')&&mediaEntries.some(media=>media.Kind==='audio'&&safeAsset(media.Path)))bubble.classList.add('audio-only');
   if(m.Reactions?.length){const badges=element('div','','reaction-badges');for(const reaction of m.Reactions){if(!(reaction.Count>0))continue;const badge=element('span','','reaction-badge'+(reaction.IsSelf?' own':''));badge.append(inlineEmoji(m.Platform,reaction.EmojiId,/^[0-9]+$/.test(reaction.EmojiId)&&Number(reaction.EmojiId)>=0x1000&&Number(reaction.EmojiId)<=0x10ffff?String.fromCodePoint(Number(reaction.EmojiId)):'#'+reaction.EmojiId),document.createTextNode(' '+reaction.Count));badges.append(badge);}referenceZone.append(badges);}
   const tools=element('div','','message-tools'),sourceTools=element('div','','image-source-tools');
   tools.append(sourceDisclosure(m),sourceTools);
   const imageGroups=new Map(),renderedGroups=new Set();
   for(const media of mediaEntries.filter(media=>media.Kind==='image'&&safeAsset(media.Path))){const id=logicalImage(media),group=imageGroups.get(id)||[];if(!group.some(item=>item.Path===media.Path))group.push(media);imageGroups.set(id,group);}
   function appendMedia(block,media,kind){
    if(media.Kind!=='image'){block.append(renderMedia(media,kind));return;}
    const id=logicalImage(media);if(renderedGroups.has(id))return;renderedGroups.add(id);
    const variants=[...(imageGroups.get(id)||[media])].sort((a,b)=>(a.Quality==='thumbnail')-(b.Quality==='thumbnail'));
    const host=element('span','','image-source-view');block.append(host);let selected=0,all=false;
    const switcher=element('button','','image-source-switch'),toggle=element('button',label('ReaderAllImageSources'),'image-source-all');
    function display(){host.replaceChildren();for(const value of (all?variants:[variants[selected]])){const node=renderMedia(value,kind);host.append(node);}switcher.textContent=label('ReaderImageSource').replace('{0}',selected+1).replace('{1}',variants.length);toggle.textContent=label(all?'ReaderOneImageSource':'ReaderAllImageSources');toggle.setAttribute('aria-pressed',String(all));}
    if(variants.length>1){switcher.type=toggle.type='button';switcher.title=label('ReaderNextImageSource');switcher.onclick=()=>{selected=(selected+1)%variants.length;all=false;display();};toggle.onclick=()=>{all=!all;display();};sourceTools.append(switcher,toggle);}
    display();
   }
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
    const playableAudio=kind==='audio'&&candidates.some(media=>media.Kind==='audio');if(playableAudio)block.classList.add('playable-audio');
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
    if (!['text','reply','system','markdown','poke','reaction','recall'].includes(kind)&&!visual&&!playableAudio) {
     block.append(element('div', label('ReaderKind_' + kind), 'kind-title'));
     if (part.Title) block.append(element('strong', part.Title));
    }
    if (kind === 'markdown') block.append(element('div', label('ReaderKind_markdown'), 'kind-title'));
    if (part.Text && (!rich || ['text','system','markdown','poke'].includes(kind))) {const text=element('span','','message-text');textWithEmoji(text,depth===0?visibleText(m,part.Text):part.Text,m.Platform);block.append(text);}
    if(kind==='poke'&&!part.Text)block.append(element('span',label('ReaderKind_poke')));
    if(kind==='recall'){const key=part.RecallRole===1?'ReaderRecallAdmin':part.RecallRole===2?'ReaderRecallOwner':'ReaderRecall';block.append(element('span',label(key).replace('{0}',part.ReferenceSender||label('ReaderUnknownSender'))));}
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
     used.add(assetIdentity(media)); appendMedia(block,media,kind);
     if (media.Quality === 'thumbnail') block.append(element('small', label('ReaderThumbnail')));
    }
    if (['image','video','audio','sticker','file'].includes(kind) && !candidates.length) block.append(element('div',
     label(part.MediaState === 'unsupported-format' ? 'ReaderUnsupportedFormat' : 'ReaderFileMissing'), 'placeholder'));
    if (kind === 'unsupported' || kind === 'unparsed' || rich) block.append(element('div', label('ReaderSourceHint'), 'card-detail'));
    return visual&&!block.hasChildNodes()?document.createDocumentFragment():block;
   }
   for (const part of parts) bubble.append(partView(part));
   for (const media of mediaEntries) {
    if (safeAsset(media.Path) && !used.has(assetIdentity(media)) && !reserved.has((media.Key||'').toLowerCase()) && !reserved.has((media.Name||'').toLowerCase())) { used.add(assetIdentity(media)); appendMedia(bubble,media,media.Kind); }
   }
   if(archiveResults)content.append(element('div',(index.conversations.find(c=>c.id===row.conversation)?.name||'')+' · '+(row.page+1),'result-location'));
   content.append(time, bubble, referenceZone, tools);
   if((hasFilters||archiveResults)&&!contextMode){const context=element('button',label('ReaderShowContext'),'show-context');context.onclick=()=>archiveResults?resultContext(row,context):showContext(m.Id,page,context);content.append(context);}
   if (!system) article.append(avatar); article.append(content); fragment.append(article);
  }
  $('messages').replaceChildren(fragment); layoutReferences(); $('messages').scrollTop = 0;
  if(!shownCount)$('messages').append(element('div',label('ReaderNoPageMatches'),'empty-results'));
  $('messages').classList.remove('enter'); void $('messages').offsetWidth; $('messages').classList.add('enter');
 }
 $('contacts-search').oninput = list;
 function go(n){if(archiveResults){searchArchive(n);return;}if(!current)return;stopSearch();page=Math.max(0,Math.min(current.pages-1,n));load();}
 $('previous').onclick=()=>go(archiveResults?archiveSearch.batch-1:page-1);$('next').onclick=()=>go(archiveResults?archiveSearch.batch+1:page+1);$('first').onclick=()=>go(0);$('last').onclick=()=>{if(current)go(archiveResults?archiveSearch.endBatch:current.pages-1);};
 $('page-go').onclick=()=>{if(!current)return;const n=Number($('page-input').value);if(Number.isInteger(n)&&n>=1&&n<=(archiveResults?archiveSearch.starts.length:current.pages))go(n-1);else $('page-input').reportValidity();};$('page-input').onkeydown=e=>{if(e.key==='Enter')$('page-go').click();};
 document.addEventListener('pointerdown',e=>{if(!e.target.closest('.identity-details,.avatar[data-profile]'))for(const card of document.querySelectorAll('.identity-details[open]'))card.open=false;});
 document.addEventListener('keydown',e=>{if(e.key==='Escape')for(const card of document.querySelectorAll('.identity-details[open]'))card.open=false;});
 document.addEventListener('focusin',e=>{if(!e.target.closest('.identity-details,.avatar[data-profile]'))for(const card of document.querySelectorAll('.identity-details[open]'))card.open=false;});
 document.addEventListener('toggle',e=>{if(e.target.matches?.('.identity-details[open]'))for(const other of document.querySelectorAll('.identity-details[open]'))if(other!==e.target)other.open=false;},true);
 const preview=$('image-preview'),picture=$('preview-image'),stage=$('image-stage');let imagePath='',imageName='',scale=1,x=0,y=0,drag=null,rotation=0,imageOrigin=null;
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
 function transform(){picture.style.transform=`translate(${x}px,${y}px) scale(${scale}) rotate(${rotation}deg)`;text('zoom-value',Math.round(scale*100)+'%');}
 function fitImage(){x=y=0;const sideways=rotation%180!==0;scale=Math.min(1,(stage.clientWidth-32)/Math.max(1,sideways?picture.naturalHeight:picture.naturalWidth),(stage.clientHeight-32)/Math.max(1,sideways?picture.naturalWidth:picture.naturalHeight));transform();}
 function imageList(){const seen=new Set();return Array.from(document.querySelectorAll('#messages img.previewable')).filter(img=>{const key=img.closest('.message')?.id+':'+assetIdentity({Kind:'image',Path:img.dataset.asset});if(seen.has(key))return false;seen.add(key);return true;});}
 function openImage(path,name,origin=null){if(!safeAsset(path))return;imagePath=path;imageName=name||'image';imageOrigin=origin||imageList().find(img=>img.dataset.asset===path);rotation=0;picture.onload=fitImage;picture.src=archiveBase+path;picture.alt=imageName;if(!preview.open){centerPreview();preview.showModal();}const list=imageList(),at=list.indexOf(imageOrigin);text('image-title',imageName+(at>=0?' · '+(at+1)+' / '+list.length:''));$('image-previous').disabled=at<=0;$('image-next').disabled=at<0||at+1>=list.length;}
 function changeImage(delta){const list=imageList(),at=list.indexOf(imageOrigin),next=list[at+delta];if(next)openImage(next.dataset.asset,next.alt,next);}
 function locateImage(){const article=imageOrigin?.closest('.message');preview.close();if(!article){actionStatus(label('ReaderReferenceMissing'));return;}if(article.resultRow)resultContext(article.resultRow,imageOrigin);else highlight(article.messageData.Id);}
 $('image-locate').onclick=locateImage;
 for(const [id,delta,key] of [['rotate-left',-90,'ReaderRotateLeft'],['rotate-right',90,'ReaderRotateRight']]){$(id).title=label(key);$(id).setAttribute('aria-label',label(key));$(id).onclick=()=>{rotation=(rotation+delta+360)%360;fitImage();};}
 async function copyImage(path=imagePath,angle=rotation){
  try{const img=new Image();img.src=(window.readerImageCopyBase?window.readerImageCopyBase+path.slice(7):archiveBase+path);await img.decode();if(img.naturalWidth*img.naturalHeight>32000000)throw Error('size');
   const canvas=document.createElement('canvas'),sideways=angle%180!==0;canvas.width=sideways?img.naturalHeight:img.naturalWidth;canvas.height=sideways?img.naturalWidth:img.naturalHeight;const ctx=canvas.getContext('2d');ctx.translate(canvas.width/2,canvas.height/2);ctx.rotate(angle*Math.PI/180);ctx.drawImage(img,-img.naturalWidth/2,-img.naturalHeight/2);
   const encoded=canvas.toDataURL('image/png').split(',')[1];if(encoded.length>32*1024*1024)throw Error('size');if(hostAction({action:'clipboard-image',data:encoded}))return;
   const blob=await new Promise(resolve=>canvas.toBlob(resolve,'image/png'));await navigator.clipboard.write([new ClipboardItem({'image/png':blob})]);actionStatus(label('ReaderCopied'));
  }catch{actionStatus(label('ReaderCopyImageFailed'));}
 }
 $('image-copy').onclick=()=>copyImage();
 function zoom(factor,cx=0,cy=0){const next=Math.max(.05,Math.min(20,scale*factor)),ratio=next/scale;x=cx-(cx-x)*ratio;y=cy-(cy-y)*ratio;scale=next;transform();}
 stage.addEventListener('wheel',e=>{e.preventDefault();const box=stage.getBoundingClientRect();zoom(Math.exp(-e.deltaY*.0015),e.clientX-box.left-box.width/2,e.clientY-box.top-box.height/2);},{passive:false});
 stage.onpointerdown=e=>{if(e.button!==0)return;drag={cx:e.clientX,cy:e.clientY,x,y};stage.setPointerCapture(e.pointerId);};stage.onpointermove=e=>{if(drag){x=drag.x+e.clientX-drag.cx;y=drag.y+e.clientY-drag.cy;transform();}};stage.onpointerup=()=>drag=null;stage.onpointercancel=()=>drag=null;
 $('zoom-in').onclick=()=>zoom(1.25);$('zoom-out').onclick=()=>zoom(.8);$('zoom-fit').onclick=fitImage;$('zoom-original').onclick=()=>{scale=1;x=y=0;transform();};picture.ondblclick=()=>{if(scale===1)fitImage();else{scale=1;x=y=0;transform();}};
 let backdropPressed=false;
 function outsidePreview(e){const box=preview.getBoundingClientRect();return e.clientX<box.left||e.clientX>box.right||e.clientY<box.top||e.clientY>box.bottom;}
 preview.addEventListener('pointerdown',e=>{backdropPressed=e.target===preview&&outsidePreview(e);});
 preview.onclick=e=>{if(backdropPressed&&e.target===preview&&outsidePreview(e))preview.close();backdropPressed=false;};
 $('image-previous').onclick=()=>changeImage(-1);$('image-next').onclick=()=>changeImage(1);$('image-close').onclick=()=>preview.close();preview.onkeydown=e=>{if(e.key==='ArrowLeft')changeImage(-1);if(e.key==='ArrowRight')changeImage(1);if(e.key==='+')zoom(1.25);if(e.key==='-')zoom(.8);};window.addEventListener('resize',()=>{if(preview.open)fitImage();});
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
  if(image){const path=image.id==='preview-image'?imagePath:image.dataset.asset,name=image.alt;menuItem('ReaderPreviewImage',()=>openImage(path,name,image.id==='preview-image'?imageOrigin:image));menuItem('ReaderCopyImage',()=>copyImage(path,image.id==='preview-image'?rotation:0));if(image.id==='preview-image')menuItem('ReaderLocateImage',locateImage);menuItem('ReaderSaveImage',()=>saveImage(path,name));}
  if(article){const m=article.messageData;menuItem('ReaderCopyMessage',()=>copyText(effectiveParts(m).map(p=>p.Text||p.Title||p.ReferenceText||'').filter(Boolean).join('')));if(m.Id)menuItem('ReaderCopyMessageId',()=>copyText(m.ServerId||m.Id));menuItem('ReaderSource',()=>{article.querySelector('details.source').open=true;});}
  menu.hidden=false;menu.style.left=Math.min(e.clientX,innerWidth-menu.offsetWidth-8)+'px';menu.style.top=Math.min(e.clientY,innerHeight-menu.offsetHeight-8)+'px';menu.querySelector('button')?.focus();
 });
 document.addEventListener('pointerdown',e=>{if(!menu.contains(e.target))hideMenu();});document.addEventListener('keydown',e=>{if(e.key==='Escape')hideMenu();if(!menu.hidden&&['ArrowDown','ArrowUp'].includes(e.key)){e.preventDefault();const buttons=Array.from(menu.querySelectorAll('button')),at=buttons.indexOf(document.activeElement);buttons[(at+(e.key==='ArrowDown'?1:-1)+buttons.length)%buttons.length].focus();}});$('messages').addEventListener('scroll',hideMenu);
 $('messages').addEventListener('wheel',()=>pinBottom=false,{passive:true});$('messages').addEventListener('pointerdown',()=>pinBottom=false);
 document.addEventListener('keydown',e=>{if((e.ctrlKey||e.metaKey)&&e.key==='f'){e.preventDefault();toggleFilters(true);$('message-search').focus();}if(!e.target.closest('input,select,textarea')&&e.altKey&&e.key==='ArrowLeft'){e.preventDefault();go(page-1);}if(!e.target.closest('input,select,textarea')&&e.altKey&&e.key==='ArrowRight'){e.preventDefault();go(page+1);}});
 list(); if (index.conversations.length) { current=index.conversations[0];try{const saved=JSON.parse(localStorage.getItem(positionKey));const found=index.conversations.find(c=>c.id===saved?.id);if(found){current=found;page=Math.max(0,Math.min(current.pages-1,Number(saved.page)||0));}}catch{}if(readerPrefs.autoLast)page=Math.max(0,current.pages-1);list();load(); }
})();
