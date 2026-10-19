const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const {parseHTML} = require('linkedom');

(async () => {
  const {window} = parseHTML('<html><body><div id="compusage-cards-preview"></div></body></html>');
  const document = window.document;
  // LinkeDOM models the DOM without launching a browser or interacting with the real clipboard.
  let receive, clipboard = '', writes = 0, calls = [];
  const time = (display, exact) => ({display,exact,date:'2026.10.16'});
  let state = {settings:{dayStart:'04:00:00',expectedLunch:'11:30:00',minimumLunchMinutes:15,splitBreakMinutes:10,historyDays:60,workbookPath:'C:\\test\\work.xlsx',fontScale:1.15,rememberView:true,language:'hu',theme:'auto',lockTimeSource:'lock'},logging:true,permissions:{readAccessRequired:false},activity:{enabled:false,running:false,registered:true},user:'Test',expanded:[],scroll:0,refreshed:'2026.10.19 14:00:00',oldest:null,workbook:{available:true,lastDate:'2026.10.15',days:1,rows:2,partials:0},days:[{key:'2026-10-16',date:'2026.10.16',weekday:'péntek',status:'Lezárt',ongoing:false,incomplete:false,issues:[],first:time('8:02','08:02:14'),last:time('16:48','16:48:09'),manual:false,none:false,undo:false,lunch:{id:'lunch',from:time('11:32','11:32:05'),to:time('11:51','11:51:34'),minutes:19},pairs:[{id:'short',from:time('10:16','10:16:03'),to:time('10:20','10:20:09'),minutes:4},{id:'lunch',from:time('11:32','11:32:05'),to:time('11:51','11:51:34'),minutes:19}],sections:[{date:'2026.10.16',from:time('8:02','08:02:14'),to:time('11:32','11:32:05'),duration:'03:30',open:false,copy:'2026.10.16\t08:02:14\t11:32:05'},{date:'2026.10.16',from:time('11:51','11:51:34'),to:time('16:48','16:48:09'),duration:'04:57',open:false,copy:'2026.10.16\t11:51:34\t16:48:09'}],copy:'2026.10.16\t08:02:14\t11:32:05\r\n2026.10.16\t11:51:34\t16:48:09',eventCopy:'events',events:[]}]};
  const clone = value => JSON.parse(JSON.stringify(value));
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

  window.chrome = {webview:{addEventListener(name,handler){receive=handler;},postMessage(message){calls.push(message);let result=true;try{switch(message.command){case 'load':case 'refresh':result=clone(state);break;case 'copy':clipboard=message.data.text;break;case 'paste':result=clipboard;break;case 'settings':state.settings=clone(message.data.settings);state.logging=message.data.logging;applyActivityReply(message);result=clone(state);break;case 'history':state.settings.historyDays=message.data.days;result=clone(state);break;case 'preview':result={token:'plan',rows:clone(state.days[0].sections),partials:[]};break;case 'commit':writes++;result={state:clone(state),backup:'C:\\test\\backup.xlsx'};break;case 'lunch':state.days[0].manual=message.data.value!==null;state.days[0].undo=true;result=clone(state);break;case 'undo':state.days[0].manual=false;state.days[0].undo=false;result=clone(state);break;case 'previewTheme':break;case 'grantReadAccess':state.permissions.readAccessRequired=false;state.logging=true;result=clone(state);break;case 'view':break;default:throw Error(message.command);}queueMicrotask(()=>receive({data:{id:message.id,ok:true,result}}));}catch(error){queueMicrotask(()=>receive({data:{id:message.id,ok:false,error:error.message}}));}}}};
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
  console.log(`${count} DOM/bridge checks passed (no browser, native clipboard, log, or user workbook access).`);
})().catch(error=>{console.error(error);process.exitCode=1;});
