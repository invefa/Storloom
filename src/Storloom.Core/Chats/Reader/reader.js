"use strict";
(() => {
 const index=window.archiveIndex, labels=index.labels, $=id=>document.getElementById(id);
 let current=null, page=0, messages=[], request=0;
 const text=(id,value)=>{$(id).textContent=value;};
 document.documentElement.lang=index.language;
 document.title=labels.ChatArchive; text('archive-title',labels.ChatArchive);
 $('contacts-search').placeholder=labels.ReaderSearch; $('message-search').placeholder=labels.ReaderPageSearch;
 $('theme').title=labels.ReaderTheme;text('theme',labels.ReaderTheme); text('previous',labels.ReaderPrevious);text('next',labels.ReaderNext);
 text('chat-title',labels.ReaderSelect);text('notice',labels.ReaderNotice+(index.unresolvedMedia?' '+labels.ReaderUnresolved+' '+index.unresolvedMedia:''));
 let dark=location.hash==='#dark'||(!location.hash&&matchMedia('(prefers-color-scheme:dark)').matches);
 document.body.classList.toggle('dark',dark);
 $('theme').onclick=()=>{dark=!dark;document.body.classList.toggle('dark',dark);};
 function list(){const term=$('contacts-search').value.toLocaleLowerCase();const fragment=document.createDocumentFragment();
  for(const chat of index.conversations){if(!chat.name.toLocaleLowerCase().includes(term))continue;
   const button=document.createElement('button');button.classList.toggle('active',current?.id===chat.id);
   const name=document.createElement('strong');name.textContent=chat.name;const meta=document.createElement('small');meta.textContent=chat.count+' '+labels.ReaderMessages;
   button.append(name,meta);button.onclick=()=>{current=chat;page=0;$('message-search').value='';list();load();};fragment.append(button);}
  $('conversations').replaceChildren(fragment);
 }
 function load(){if(!current)return;const id=++request;text('chat-title',current.name);text('chat-meta',current.count+' '+labels.ReaderMessages);
  $('previous').disabled=page===0;$('next').disabled=page+1>=current.pages;text('page',(page+1)+' / '+current.pages);
  messages=[];$('messages').replaceChildren();const script=document.createElement('script');script.src='conversations/'+current.id+'-'+page+'.js';
  script.onload=()=>script.remove();script.onerror=()=>{script.remove();if(request===id)text('notice',labels.ReaderMissingMedia);};document.head.append(script);
 }
 window.receivePage=(conversation,p,items)=>{if(current?.id!==conversation||p!==page)return;messages=items;render();};
 function render(){const fragment=document.createDocumentFragment(),term=$('message-search').value.toLocaleLowerCase();
  for(const m of messages){if(term&&!m.Text.toLocaleLowerCase().includes(term)&&!(m.SenderName||m.Sender).toLocaleLowerCase().includes(term))continue;
   const article=document.createElement('article');article.className='message'+(m.IsSelf?' self':'');const avatar=document.createElement('div');avatar.className='avatar';avatar.textContent=Array.from(m.SenderName||m.Sender||'?')[0];
   const content=document.createElement('div');content.className='content';const time=document.createElement('div');time.className='timestamp';
   const date=new Date(m.Timestamp*1000);time.textContent=(m.SenderName||m.Sender)+'  '+(Number.isNaN(date.valueOf())?m.Timestamp:date.toLocaleString());
   const bubble=document.createElement('div');bubble.className='bubble';const xml=m.Text.trimStart().startsWith('<');
   if(xml){const detail=document.createElement('details');const summary=document.createElement('summary');summary.textContent='XML';const pre=document.createElement('pre');pre.textContent=m.Text;detail.append(summary,pre);bubble.append(detail);}
   else bubble.append(document.createTextNode(m.Text));
   for(const media of m.Media||[]){if(!/^assets\/[a-f0-9]{64}\.(png|jpg|jpeg|gif|webp|bmp|mp4|webm|mp3|wav|ogg)$/.test(media.Path))continue;
    const element=document.createElement(media.Kind==='video'?'video':media.Kind==='audio'?'audio':'img');element.src=media.Path;
    if(element.tagName==='IMG'){element.alt=media.Name;element.loading='lazy';}else{element.controls=true;element.preload='none';}
    element.onerror=()=>{const fallback=document.createElement('div');fallback.className='missing';fallback.textContent=labels.ReaderMissingMedia;element.replaceWith(fallback);};bubble.append(element);
   }
   if(!(m.Media||[]).length&&[3,34,43,47].includes(m.Type%4294967296)){const note=document.createElement('div');note.className='missing';note.textContent=labels.ReaderMissingMedia;bubble.append(note);}
   content.append(time,bubble);article.append(avatar,content);fragment.append(article);
  }$('messages').replaceChildren(fragment);$('messages').scrollTop=0;
 }
 $('contacts-search').oninput=list;$('message-search').oninput=render;
 $('previous').onclick=()=>{if(page>0){page--;load();}};$('next').onclick=()=>{if(current&&page+1<current.pages){page++;load();}};
 list();if(index.conversations.length){current=index.conversations[0];list();load();}
})();
