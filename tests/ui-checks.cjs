const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const {parseHTML} = require('linkedom');

(async () => {
  const {window} = parseHTML('<html><body><div id="compusage-cards-preview"></div></body></html>');
  const document = window.document;
  // LinkeDOM models the DOM without launching a browser or interacting with the real clipboard.
  let receive, clipboard = '', writes = 0, calls = [], meetingUndo=null, failComment=false, previewPartials=[];
  const time = (display, exact) => ({display,exact,date:'2026.10.16'});
  let state = {settings:{dayStart:'04:00:00',expectedLunch:'11:30:00',minimumLunchMinutes:15,splitBreakMinutes:10,ignoreDailyMeeting:false,meetingStartFrom:'09:25:00',meetingStartUntil:'09:40:00',maximumMeetingMinutes:20,historyDays:60,workbookPath:'C:\\test\\work.xlsx',fontScale:1.15,rememberView:true,language:'hu',theme:'auto',lockTimeSource:'lock'},logging:true,permissions:{readAccessRequired:false},activity:{enabled:false,running:false,registered:true},user:'Test',expanded:[],scroll:0,refreshed:'2026.10.19 14:00:00',oldest:null,workbook:{available:true,lastDate:'2026.10.15',days:1,rows:2,partials:0},days:[{key:'2026-10-16',date:'2026.10.16',weekday:'péntek',status:'Lezárt',ongoing:false,incomplete:false,issues:[],first:time('8:02','08:02:14'),last:time('16:48','16:48:09'),manual:false,none:false,undo:false,lunch:{id:'lunch',from:time('11:32','11:32:05'),to:time('11:51','11:51:34'),minutes:19},pairs:[{id:'short',from:time('10:16','10:16:03'),to:time('10:20','10:20:09'),minutes:4},{id:'lunch',from:time('11:32','11:32:05'),to:time('11:51','11:51:34'),minutes:19}],sections:[{date:'2026.10.16',from:time('8:02','08:02:14'),to:time('11:32','11:32:05'),duration:'03:30',open:false,copy:'2026.10.16\t08:02:14\t11:32:05'},{date:'2026.10.16',from:time('11:51','11:51:34'),to:time('16:48','16:48:09'),duration:'04:57',open:false,copy:'2026.10.16\t11:51:34\t16:48:09'}],copy:'2026.10.16\t08:02:14\t11:32:05\r\n2026.10.16\t11:51:34\t16:48:09',eventCopy:'events',events:[]}]};
  const clone = value => JSON.parse(JSON.stringify(value));
  for(const day of state.days)day.sections.forEach((s,i)=>{s.id=day.key+'|'+i;s.comment='';s.durationExact=i?'4:56:35':'3:29:51';});
  function applyActivityReply(message) {
    state.activity.enabled=message.data.activityEnabled;
    state.activity.running=message.data.activityEnabled;
    const day=state.days[0], active=state.settings.lockTimeSource==='activity';
    day.lastFromActivity=active;
    day.last=active?time('16:40','16:40:37'):time('16:48','16:48:09');
    day.sections[1].to=clone(day.last);
    day.sections[1].copy=`2026.10.16\t11:51:34\t${day.last.exact}`;
    day.copy=day.sections.map(s=>s.copy).join('\r\n');
  }

  window.chrome = {webview:{addEventListener(name,handler){receive=handler;},postMessage(message){calls.push(message);let result=true;try{switch(message.command){case 'load':case 'refresh':result=clone(state);break;case 'copy':clipboard=message.data.text;break;case 'paste':result=clipboard;break;case 'settings':state.settings=clone(message.data.settings);state.logging=message.data.logging;applyActivityReply(message);result=clone(state);break;case 'history':state.settings.historyDays=message.data.days;result=clone(state);break;case 'commentsPending':case 'commentsFailed':break;case 'sectionComment':{if(failComment)throw Error('Comment save failed');const row=state.days.flatMap(d=>d.sections).find(s=>s.id===message.data.sectionId);row.comment=message.data.comment;break;}case 'preview':result={token:'plan',rows:clone(state.days[0].sections),partials:clone(previewPartials)};break;case 'commit':writes++;result={state:clone(state),backup:'C:\\test\\backup.xlsx'};break;case 'lunch':state.days[0].manual=message.data.value!==null;state.days[0].undo=true;result=clone(state);break;case 'meeting':{
      const day=state.days[0];meetingUndo=clone(day);day.manualMeeting=message.data.value!==null;day.noMeeting=message.data.value==='none';
      const pair=message.data.value===null?day.pairs[0]:day.pairs.find(p=>p.id===message.data.value);
      day.meeting=pair?{id:pair.id,from:clone(pair.from),to:clone(pair.to),minutes:pair.minutes}:null;
      if(day.meeting?.id===day.lunch?.id){day.lunch=null;day.manual=false;}day.undo=true;result=clone(state);break;
    }case 'undo':if(meetingUndo){state.days[0]=meetingUndo;meetingUndo=null;}state.days[0].manual=false;state.days[0].undo=false;result=clone(state);break;case 'breakReason':{const pair=state.days.flatMap(d=>d.pairs).find(p=>p.id===message.data.pairId);if(!pair)throw Error('Nincs ilyen szünetpár.');pair.reason=message.data.reason==='none'?null:message.data.reason;pair.reasonText=message.data.text||'';pair.reasonManual=true;result=clone(state);break;}case 'previewTheme':break;case 'grantReadAccess':state.permissions.readAccessRequired=false;state.logging=true;result=clone(state);break;case 'view':break;default:throw Error(message.command);}queueMicrotask(()=>receive({data:{id:message.id,ok:true,result}}));}catch(error){queueMicrotask(()=>receive({data:{id:message.id,ok:false,error:error.message}}));}}}};
  window.scrollY=0;window.scrollTo=(_x,y)=>{window.scrollY=y;};window.getSelection=()=>({toString:()=>''});
  const context={window,document,NodeFilter:{SHOW_TEXT:4},Event:window.Event,console,setTimeout:()=>0,clearTimeout(){},Map,Set};
  vm.runInNewContext(fs.readFileSync('src/CompUsage.App/Ui/i18n.js','utf8'),context);
  vm.runInNewContext(fs.readFileSync('src/CompUsage.App/Ui/app.js','utf8'),context);
  const flush=()=>new Promise(resolve=>setImmediate(resolve));
  await flush();
  let count=0;
  async function check(name,fn){await fn();count++;console.log('PASS '+name);}
  function node(selector){const n=document.querySelector(selector);assert.ok(n,'Missing '+selector);return n;}
  async function click(selector){node(selector).dispatchEvent(new window.Event('click',{bubbles:true}));await flush();}
  async function input(selector,value){const n=node(selector);Object.defineProperty(n,'value',{value,writable:true,configurable:true});n.dispatchEvent(new window.Event('input',{bubbles:true}));await flush();}
  await check('Native bridge loads real-state-shaped data without sample records',()=>{assert.equal(document.querySelectorAll('[data-day]').length,1);assert.ok(calls.some(c=>c.command==='refresh'));assert.match(document.body.textContent,/Munkaszakaszok:/);});
  await check('Disabled logging warns at the top and opens its settings without enabling it',async()=>{assert.equal(document.querySelector('[data-logging-warning]'),null);state.logging=null;await click('[data-refresh]');assert.equal(document.querySelector('[data-logging-warning]'),null);state.logging=false;await click('[data-refresh]');assert.match(node('[data-logging-warning]').textContent,/nem kerülnek rögzítésre/);await click('[data-settings="logging"]');assert.equal(node('[data-tab="time"]').getAttribute('aria-selected'),'true');assert.equal(node('#panel-time').hasAttribute('hidden'),false);assert.equal(state.logging,false);const toggle=node('[data-logging]');toggle.checked=true;toggle.dispatchEvent(new window.Event('input',{bubbles:true}));await click('[data-save]');assert.equal(state.logging,true);assert.equal(document.querySelector('[data-logging-warning]'),null);});
  await check('Large-time and work-section buttons copy exact native payloads',async()=>{await click('[data-copy="08:02:14"]');assert.equal(clipboard,'08:02:14');const section=document.querySelector('.cu-work-copy');section.dispatchEvent(new window.Event('click',{bubbles:true}));await flush();assert.equal(clipboard,'2026.10.16\t08:02:14\t11:32:05');});
  await check('Daily copy has no header and retains the two work sections',async()=>{await click('.cu-day-head>[data-copy]');assert.equal(clipboard,state.days[0].copy);assert.equal(clipboard.split('\r\n').length,2);});
  await check('Expanding a day keeps the event choices and undo actions',async()=>{await click('[data-expand]');assert.equal(document.querySelectorAll('[data-lunch]').length,3);const radio=node('[data-lunch][value="short"]');radio.dispatchEvent(new window.Event('change',{bubbles:true}));await flush();assert.ok(calls.some(c=>c.command==='lunch'&&c.data.value==='short'));assert.ok(document.querySelector('[data-undo]'));await click('[data-undo]');});
  await check('Cancel discards edited settings and restores the theme',async()=>{await click('[data-settings]');await input('[data-setting="dayStart"]','05:00');await click('[data-tab="display"]');await input('[data-setting="theme"]','dark');assert.equal(document.documentElement.style.colorScheme,'dark');assert.equal(calls.findLast(c=>c.command==='previewTheme').data.theme,'dark');await click('[data-cancel]');assert.equal(calls.findLast(c=>c.command==='previewTheme').data.theme,'auto');assert.equal(state.settings.dayStart,'04:00:00');assert.equal(document.documentElement.style.colorScheme,'light dark');});
  await check('Saved English language and light theme update the whole rendered UI',async()=>{await click('[data-settings]');await click('[data-tab="display"]');await input('[data-setting="language"]','en');await input('[data-setting="theme"]','light');await click('[data-save]');assert.equal(state.settings.language,'en');assert.equal(document.documentElement.lang,'en');assert.equal(document.documentElement.style.colorScheme,'light');assert.equal(node('h1').textContent,'Work log');assert.match(document.body.textContent,/First unlock/);assert.match(document.body.textContent,/Work sections:/);assert.equal(node('[data-copy="08:02:14"]').getAttribute('data-copy'),'08:02:14');});
  await check('English settings retain Hungarian and English choices and all three themes',async()=>{await click('[data-settings]');await click('[data-tab="display"]');assert.equal(node('[data-setting="theme"]').querySelectorAll('option').length,3);assert.match(document.body.textContent,/Follow OS/);assert.match(document.body.textContent,/Language/);await click('[data-cancel]');});
  await check('Disabled-logging warning is localized and remains a settings shortcut',async()=>{state.logging=false;await click('[data-refresh]');const warning=node('[data-logging-warning]');assert.match(warning.textContent,/logging is disabled/);assert.match(warning.textContent,/new locks and unlocks are not recorded/);assert.match(warning.textContent,/Enable logging in Settings/);await click('[data-settings="logging"]');assert.equal(node('[data-tab="time"]').getAttribute('aria-selected'),'true');assert.match(node('#panel-time').textContent,/Windows lock\/unlock logging/);await click('[data-cancel]');state.logging=true;await click('[data-refresh]');assert.equal(document.querySelector('[data-logging-warning]'),null);});
  await check('Activity monitor defaults off and its settings switch the endpoint used by copy actions',async()=>{
    assert.equal(state.activity.enabled,false);
    await click('[data-settings]');
    assert.equal(Boolean(node('[data-activity-enabled]').checked),false);
    assert.match(node('#panel-time').textContent,/Disabled by default/);
    const toggle=node('[data-activity-enabled]');toggle.checked=true;toggle.dispatchEvent(new window.Event('input',{bubbles:true}));
    await input('[data-setting="lockTimeSource"]','activity');await click('[data-save]');
    assert.equal(calls.findLast(c=>c.command==='settings').data.activityEnabled,true);
    assert.match(node('[data-day]').textContent,/Last input/);
    assert.match(document.body.textContent,/Activity monitor running/);
    await click('[data-copy="16:40:37"]');assert.equal(clipboard,'16:40:37');
    const sections=document.querySelectorAll('.cu-work-copy');sections[1].dispatchEvent(new window.Event('click',{bubbles:true}));await flush();
    assert.equal(clipboard,'2026.10.16\t11:51:34\t16:40:37');
    await click('[data-settings]');const off=node('[data-activity-enabled]');off.checked=false;off.dispatchEvent(new window.Event('input',{bubbles:true}));
    await input('[data-setting="lockTimeSource"]','lock');await click('[data-save]');
    assert.equal(state.activity.enabled,false);assert.match(document.body.textContent,/Activity monitor disabled/);
    assert.ok(document.querySelector('[data-copy="16:48:09"]'));
  });
  await check('Activity fallback and exact observation details remain visible and selectable',async()=>{
    state.settings.lockTimeSource='activity';state.days[0].lastFromActivity=false;
    state.days[0].pairs[0].activity={lastInput:time('10:12','10:12:23'),idle:'0:03:40'};
    await click('[data-refresh]');
    assert.match(document.body.textContent,/No activity data · using lock time/);
    assert.ok(document.querySelector('input[value="0:03:40"]'));
    await click('[data-copy="10:12:23"]');assert.equal(clipboard,'10:12:23');
    state.settings.lockTimeSource='lock';await click('[data-refresh]');
  });
  await check('Read access is configured only by its explicit settings action, preserving unknown audit state on ordinary saves',async()=>{
    state.permissions.readAccessRequired=true;state.logging=null;await click('[data-refresh]');
    assert.ok(document.querySelector('[data-read-access-warning]'));
    const grantCount=calls.filter(c=>c.command==='grantReadAccess').length;
    await click('[data-settings="permissions"]');assert.equal(node('[data-logging]').indeterminate,true);
    assert.match(node('#panel-time').textContent,/entire Security log/);
    await click('[data-save]');assert.equal(calls.findLast(c=>c.command==='settings').data.logging,null);
    assert.equal(calls.filter(c=>c.command==='grantReadAccess').length,grantCount);
    await click('[data-settings="permissions"]');await click('[data-grant-read-access]');
    assert.equal(calls.filter(c=>c.command==='grantReadAccess').length,grantCount+1);
    await click('[data-cancel]');assert.equal(document.querySelector('[data-read-access-warning]'),null);
  });
  await check('Excel preview never writes until the commit button is pressed',async()=>{await click('[data-append]');assert.equal(writes,0);assert.ok(document.querySelector('[data-commit]'));await click('[data-commit]');assert.equal(writes,1);assert.equal(calls.findLast(c=>c.command==='commit').data.token,'plan');});
  await check('Refresh preserves expanded days and the current scroll position',async()=>{window.scrollY=150;await click('[data-refresh]');assert.equal(window.scrollY,150);assert.equal(node('[data-expand]').getAttribute('aria-expanded'),'true');});
  await check('Localization never rewrites clipboard attributes or user-entered paths',()=>{const div=document.createElement('div');div.innerHTML='<input value="C:\\Munkaidőnapló\\x.xlsx"><button data-copy="Dátum">Dátum</button>';window.i18n.apply(div,'en');assert.equal(div.querySelector('button').textContent,'Date');assert.equal(div.querySelector('button').getAttribute('data-copy'),'Dátum');assert.equal(div.querySelector('input').getAttribute('value'),'C:\\Munkaidőnapló\\x.xlsx');});
  await check('Daily meeting is off by default and its dedicated tab saves the exact defaults and enabled state',async()=>{
    assert.equal(state.settings.ignoreDailyMeeting,false);assert.equal(document.querySelector('[data-meeting-summary]'),null);
    await click('[data-settings]');await click('[data-tab="meeting"]');
    assert.match(node('#panel-meeting').textContent,/Ignore daily meeting/);
    assert.equal(Boolean(node('[data-setting="ignoreDailyMeeting"]').checked),false);
    assert.equal(node('[data-setting="meetingStartFrom"]').value,'09:25');assert.equal(node('[data-setting="meetingStartUntil"]').value,'09:40');
    assert.equal(node('[data-setting="maximumMeetingMinutes"]').value,'20');
    const toggle=node('[data-setting="ignoreDailyMeeting"]');toggle.checked=true;toggle.dispatchEvent(new window.Event('input',{bubbles:true}));
    await click('[data-save]');
    const saved=calls.findLast(c=>c.command==='settings').data.settings;
    assert.equal(saved.ignoreDailyMeeting,true);assert.equal(saved.meetingStartFrom,'09:25:00');assert.equal(saved.meetingStartUntil,'09:40:00');assert.equal(saved.maximumMeetingMinutes,20);
    assert.ok(document.querySelector('[data-meeting-summary]'));assert.ok(document.querySelector('[data-meeting]'));
  });
  await check('Daily meeting manual choice, undo, no-meeting and automatic estimate use the native bridge',async()=>{
    const original=clone(state.days[0]), originalLunch=original.lunch;
    const select=node('[data-meeting][value="lunch"]');select.checked=true;select.dispatchEvent(new window.Event('change',{bubbles:true}));await flush();
    const call=calls.findLast(c=>c.command==='meeting');assert.equal(call.data.date,'2026-10-16');assert.equal(call.data.value,'lunch');
    assert.equal(state.days[0].lunch,null);assert.equal(node('[data-meeting][value="lunch"]').hasAttribute('checked'),true);
    assert.match(node('[data-meeting-summary]').textContent,/Counts as work/);assert.match(node('[data-meeting-summary]').textContent,/Manual selection/);
    await click('[data-undo]');assert.equal(state.days[0].lunch.id,originalLunch.id);
    let radio=node('[data-meeting][value="none"]');radio.checked=true;radio.dispatchEvent(new window.Event('change',{bubbles:true}));await flush();
    assert.equal(calls.findLast(c=>c.command==='meeting').data.value,'none');assert.match(node('[data-meeting-summary]').textContent,/No daily meeting/);
    await click('[data-meeting-auto]');assert.equal(calls.findLast(c=>c.command==='meeting').data.value,null);
    assert.match(node('[data-meeting-summary]').textContent,/Estimated/);
    const exact=state.days[0].meeting.from.exact;await click(`[data-copy="${exact}"]`);assert.equal(clipboard,exact);
    state.settings.language='hu';await click('[data-refresh]');assert.match(node('[data-meeting-summary]').textContent,/Napi meeting/);assert.match(node('[data-meeting-summary]').textContent,/Munkaidőnek számít/);
    state.settings.language='en';state.settings.ignoreDailyMeeting=false;state.days[0]=original;meetingUndo=null;await click('[data-refresh]');
    assert.equal(document.querySelector('[data-meeting-summary]'),null);assert.equal(document.querySelector('[data-meeting]'),null);
  });
  await check('Cancel keeps daily meeting settings unchanged and invalid meeting times cannot reach native settings',async()=>{
    await click('[data-settings]');await click('[data-tab="meeting"]');await input('[data-setting="meetingStartUntil"]','09:55');await click('[data-cancel]');
    assert.equal(state.settings.meetingStartUntil,'09:40:00');
    await click('[data-settings]');await click('[data-tab="meeting"]');await input('[data-setting="meetingStartFrom"]','invalid');
    const count=calls.filter(c=>c.command==='settings').length;await click('[data-save]');
    assert.equal(calls.filter(c=>c.command==='settings').length,count);assert.match(node('[data-dialog-error]').textContent,/HH:mm/);await click('[data-cancel]');
  });
  await check('Expandable work-section table precedes raw events and copies individual fields and only three time columns',async()=>{
    state.days[0].sections[1].open=true;await click('[data-refresh]');
    const details=node('[data-section-details]');assert.match(details.querySelector('summary').textContent,/Work sections \(2\)/);
    const children=[...details.parentElement.children];assert.ok(children.indexOf(details)<children.findIndex(n=>n.querySelector('summary')?.textContent.startsWith('All log events')));
    assert.equal(details.querySelectorAll('tbody tr').length,2);assert.equal(details.querySelectorAll('[data-section-copy]:disabled').length,1);
    assert.equal(details.querySelectorAll('[data-comment-copy]').length,2);
    const status=node('[data-comment-status="2026-10-16|0"]');
    assert.equal(status.parentElement.className,'cu-comment-actions');
    assert.equal(status.previousElementSibling.getAttribute('data-comment-copy'),'2026-10-16|0');
    assert.equal(status.parentElement.parentElement.querySelector('textarea').getAttribute('data-section-comment'),'2026-10-16|0');
    assert.equal(details.hasAttribute('open'),true);assert.equal(node('[data-pair-details]').hasAttribute('open'),false);
    assert.match(node('[data-pair-details] summary').textContent,/Lock \/ unlock pairs \(\d+\)/);
    details.open=false;details.dispatchEvent(new window.Event('toggle',{bubbles:true}));await click('[data-refresh]');assert.equal(node('[data-section-details]').hasAttribute('open'),false);
    const pairs=node('[data-pair-details]');pairs.open=true;pairs.dispatchEvent(new window.Event('toggle',{bubbles:true}));
    const restore=node('[data-section-details]');restore.open=true;restore.dispatchEvent(new window.Event('toggle',{bubbles:true}));
    await click('[data-refresh]');assert.equal(node('[data-section-details]').hasAttribute('open'),true);assert.equal(node('[data-pair-details]').hasAttribute('open'),true);
    assert.equal(document.querySelector('.cu-footer'),null);
    await click('[data-section-copy="2026-10-16|0"]');assert.equal(clipboard,'2026.10.16\t08:02:14\t11:32:05');
    await click('[data-copy="3:29:51"]');assert.equal(clipboard,'3:29:51');state.days[0].sections[1].open=false;
  });
  await check('Comments save without replacing the editor, survive refresh, but never reach the Excel preview, which writes only Date/From/To',async()=>{
    const editor=node('[data-section-comment="2026-10-16|0"]');const comment='Árvíztűrő "review"\tnext\nsecond line';
    editor.value=comment;editor.dispatchEvent(new window.Event('input',{bubbles:true}));await flush();await flush();
    assert.equal(node('[data-section-comment="2026-10-16|0"]'),editor);assert.equal(calls.findLast(c=>c.command==='sectionComment').data.comment,comment);
    assert.equal(node('[data-comment-status="2026-10-16|0"]').textContent,'Saved');
    await click('[data-comment-copy="2026-10-16|0"]');assert.equal(clipboard,comment);
    await click('[data-section-copy="2026-10-16|0"]');assert.equal(clipboard,'2026.10.16\t08:02:14\t11:32:05');
    await click('[data-refresh]');assert.equal(node('[data-section-comment="2026-10-16|0"]').value,comment);
    await click('[data-append]');assert.ok(!node('[data-import]').textContent.includes(comment));assert.match(node('[data-import]').textContent,/A–C/);assert.equal(node('[data-import] thead').textContent,'DateFromTo');await click('[data-close-plan]');
    const updated=node('[data-section-comment="2026-10-16|0"]');updated.value='Hossz';updated.dispatchEvent(new window.Event('input',{bubbles:true}));
    
    state.settings.language='hu';await click('[data-refresh]');assert.match(node('[data-section-details] summary').textContent,/Munkaszakaszok/);
    assert.equal(node('[data-section-comment="2026-10-16|0"]').value,'Hossz');state.settings.language='en';await click('[data-refresh]');
  });
  await check('Failed comment saves retain the draft and block export until retry succeeds; close requests flush drafts',async()=>{
    failComment=true;const editor=node('[data-section-comment="2026-10-16|0"]');editor.value='Unsaved note';editor.dispatchEvent(new window.Event('input',{bubbles:true}));await flush();await flush();
    assert.equal(editor.value,'Unsaved note');assert.match(node('[data-comment-status="2026-10-16|0"]').textContent,/Save failed/);
    const previews=calls.filter(c=>c.command==='preview').length;await click('[data-append]');assert.equal(calls.filter(c=>c.command==='preview').length,previews);
    failComment=false;receive({data:{command:'saveCommentsBeforeClose'}});await flush();await flush();
    assert.equal(calls.findLast(c=>c.command==='commentsPending').data.pending,false);assert.equal(state.days[0].sections[0].comment,'Unsaved note');
    await click('[data-append]');assert.ok(node('[data-import] thead'));await click('[data-close-plan]');
  });
  await check('Session-only endpoints have accurate labels and localized raw events without changing exact copy data',async()=>{
    const day=state.days[0];day.hasSessionEvents=true;day.firstFromSession=true;day.lastFromSession=true;day.lastFromActivity=false;
    day.events=[{time:time('8:02','08:02:14'),eventId:4778,label:'Munkamenet csatlakoztatása',isLock:false},
      {time:time('16:48','16:48:09'),eventId:4779,label:'Munkamenet leválasztása',isLock:true}];
    day.eventCopy='2026.10.16\t08:02:14\tMunkamenet csatlakoztatása\t4778';
    await click('[data-refresh]');
    assert.match(document.body.textContent,/First connection/);assert.match(document.body.textContent,/Last disconnect/);
    assert.match(document.body.textContent,/Session connected/);assert.match(document.body.textContent,/Session disconnected/);
    assert.match(document.body.textContent,/Concurrent lock\/unlock events count once/);
    await click('[data-copy="08:02:14"]');assert.equal(clipboard,'08:02:14');
    const logButton=[...document.querySelectorAll('[data-copy]')].find(b=>b.getAttribute('data-copy')===day.eventCopy);
    assert.ok(logButton);logButton.dispatchEvent(new window.Event('click',{bubbles:true}));await flush();assert.equal(clipboard,day.eventCopy);
    state.settings.language='hu';await click('[data-refresh]');
    assert.match(document.body.textContent,/Első kapcsolódás/);assert.match(document.body.textContent,/Utolsó leválasztás/);
    assert.match(document.body.textContent,/Munkamenet csatlakoztatása/);
  });
  await check('Sign-in and shutdown endpoints have accurate localized labels',async()=>{
    const day=state.days[0];day.hasSessionEvents=false;day.hasSignInEvents=true;day.firstFromSession=false;day.lastFromSession=false;
    day.firstFromSignIn=true;day.lastFromSignOut=true;day.lastFromActivity=false;
    day.events=[{time:time('14:46','14:46:50'),eventId:4624,label:'Bejelentkezés',isLock:false},
      {time:time('17:33','17:33:07'),eventId:4647,label:'Kijelentkezés / leállítás',isLock:true}];
    state.settings.language='en';await click('[data-refresh]');
    assert.match(document.body.textContent,/First sign-in/);assert.match(document.body.textContent,/Sign-out \/ shutdown/);
    assert.match(document.body.textContent,/Sign-in/);assert.match(document.body.textContent,/the new session continues the same day/);
    state.settings.language='hu';await click('[data-refresh]');
    assert.match(document.body.textContent,/Első bejelentkezés/);assert.match(document.body.textContent,/Kijelentkezés \/ leállítás/);
  });
  await check('Details append is disabled with a reason when no closed day can be appended',async()=>{
    const original={...state.workbook};state.workbook={...original,days:0,rows:0,partials:0};await click('[data-refresh]');
    assert.equal(node('[data-append]').disabled,true);assert.match(node('[data-append-empty]').textContent,/Nincs hozzáfűzhető lezárt nap/);
    state.workbook=original;await click('[data-refresh]');assert.equal(node('[data-append]').disabled,false);assert.equal(document.querySelector('[data-append-empty]'),null);
  });
  await check('A partial preview shows the open section and the following rows of the day',async()=>{
    const section=state.days[0].sections[0];
    previewPartials=[{row:2,section:{...section,open:false},following:[{...section,id:'f1',open:true}]}];
    await click('[data-append]');const text=node('[data-import]').textContent;
    assert.match(text,/Utána új sorként/);assert.equal(document.querySelectorAll('[data-partial-following]').length,1);assert.match(text,/üresen marad \(folyamatban\)/);
    assert.match(text,/legfeljebb 3 perccel/);previewPartials=[];await click('[data-close-plan]');
  });
  await check('Activation and the minute timer refresh silently, never over an open preview, and are throttled',async()=>{
    const refreshes=()=>calls.filter(c=>c.command==='refresh').length;
    await click('[data-append]');const before=refreshes();
    receive({data:{command:'autoRefresh'}});await flush();assert.equal(refreshes(),before);
    await click('[data-close-plan]');const toast=node('[data-toast]');toast.hidden=true;
    receive({data:{command:'autoRefresh'}});await flush();await flush();
    assert.equal(refreshes(),before+1);assert.equal(toast.hidden,true);
    receive({data:{command:'autoRefresh'}});await flush();assert.equal(refreshes(),before+1);
  });
  await check('Sleep and unexpected-shutdown endpoints use the system event labels',async()=>{
    const day=state.days[0];day.hasSignInEvents=false;day.hasSystemEvents=true;day.firstFromSignIn=false;day.lastFromSignOut=false;
    day.firstSystem='Ébredés';day.lastSystem='Váratlan leállás (becsült)';
    day.events=[{time:time('8:10','08:10:04'),eventId:1,label:'Ébredés',isLock:false},
      {time:time('15:31','15:31:19'),eventId:6008,label:'Váratlan leállás (becsült)',isLock:true}];
    state.settings.language='en';await click('[data-refresh]');
    assert.match(document.body.textContent,/Wake/);assert.match(document.body.textContent,/Unexpected shutdown \(estimated\)/);
    assert.match(document.body.textContent,/its time is estimated/);
    state.settings.language='hu';await click('[data-refresh]');
    assert.match(document.body.textContent,/Váratlan leállás \(becsült\)/);
  });
  await check('Break prompts default on in their own tab and the pairs table edits the reason, including free text',async()=>{
    await click('[data-settings]');await click('[data-tab="breaks"]');
    assert.equal(node('#panel-breaks').hasAttribute('hidden'),false);
    assert.ok(node('[data-setting="breakPrompt"]').hasAttribute('checked'));
    assert.equal(node('[data-setting="breakPromptSeconds"]').value,'40');assert.equal(node('[data-setting="coffeeBreakMaxMinutes"]').value,'25');
    assert.match(node('#panel-breaks').textContent,/\(10 perc\)/);
    await input('[data-setting="breakPromptSeconds"]','45');await input('[data-setting="coffeeBreakMaxMinutes"]','20');await click('[data-save]');
    const saved=calls.findLast(c=>c.command==='settings').data.settings;
    assert.equal(saved.breakPrompt,true);assert.equal(saved.breakPromptSeconds,45);assert.equal(saved.coffeeBreakMaxMinutes,20);
    if(!document.querySelector('[data-break-reason]'))await click('[data-expand]');
    const pair=state.days[0].pairs[1];pair.reason='coffee';pair.reasonAutomatic=false;await click('[data-refresh]');
    if(!document.querySelector('[data-break-reason]'))await click('[data-expand]');
    assert.equal(node(`[data-break-reason="${pair.id}"]`).value,'coffee');assert.match(node('.cu-reason-cell').parentElement.parentElement.textContent,/Felugró ablakból/);
    const select=node(`[data-break-reason="${pair.id}"]`);Object.defineProperty(select,'value',{value:'other',writable:true,configurable:true});select.dispatchEvent(new window.Event('change',{bubbles:true}));await flush();
    assert.equal(JSON.stringify(calls.findLast(c=>c.command==='breakReason').data),JSON.stringify({pairId:pair.id,reason:'other'}));
    const text=node(`[data-break-text="${pair.id}"]`);Object.defineProperty(text,'value',{value:'Orvos',writable:true,configurable:true});text.dispatchEvent(new window.Event('change',{bubbles:true}));await flush();
    assert.equal(JSON.stringify(calls.findLast(c=>c.command==='breakReason').data),JSON.stringify({pairId:pair.id,reason:'other',text:'Orvos'}));
    assert.match(document.body.textContent,/Kézi választás/);
    await click(`[data-break-reset="${pair.id}"]`);
    assert.equal(JSON.stringify(calls.findLast(c=>c.command==='breakReason').data),JSON.stringify({pairId:pair.id,reason:null}));
  });
  await check('The monitor status counts break prompts as a reason to run',async()=>{
    state.activity={enabled:false,running:true,prompts:true,registered:true};await click('[data-refresh]');
    assert.match(node('.cu-activity-status').textContent,/Háttérfigyelő működik/);
    state.activity={enabled:false,running:false,prompts:true,registered:true};await click('[data-refresh]');
    assert.ok(document.querySelector('[data-start-activity]'));
    state.activity={enabled:false,running:false,prompts:false,registered:true};await click('[data-refresh]');
    assert.match(node('.cu-activity-status').textContent,/Háttérfigyelő kikapcsolva/);assert.equal(document.querySelector('[data-start-activity]'),null);
  });
  console.log(`${count} DOM/bridge checks passed (no browser, native clipboard, log, or user workbook access).`);
})().catch(error=>{console.error(error);process.exitCode=1;});
