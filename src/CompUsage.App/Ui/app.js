'use strict';
(() => {
  const root = document.getElementById('compusage-cards-preview');
  // Work sections start open and remember being closed; lock/unlock pairs start closed and remember being opened.
  const pending = new Map(), commentDrafts = new Map(), closedSections = new Set(), pairDetails = new Set();
  let savingComments = null;
  let sequence = 0, state, expanded = new Set(), draft = null, tab = 'time', plan = null;
  let toastTimer, scrollTimer, busy = false, started = false, autoRun = null, lastAutoRefresh = 0;
  const esc = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
  const icon = name => `<i data-lucide="${name}" aria-hidden="true"></i>`;
  const field = (value, label) => `<input type="text" class="cu-value" readonly value="${esc(value)}" aria-label="${esc(label)}" style="width:${String(value).length + .05}ch">`;
  const copyButton = (value, label, className = 'cu-value-copy') => `<button class="cu-quiet ${className}" data-copy="${esc(value)}" aria-label="${esc(label)} másolása" title="Másolás: ${esc(value)}">${icon('copy')}</button>`;
  const time = (value, label, exactIcon = false, date = '') => value ? `<span class="cu-exact">${field(value.display, label)}${date && value.date !== date ? `<span class="cu-next" title="${esc(value.date)}">+1</span>` : ''}${exactIcon ? copyButton(value.exact, label) : ''}</span>` : '—';
  function rpc(command, data = {}) {
    return new Promise((resolve, reject) => {
      const id = String(++sequence);
      const timeout = setTimeout(() => { pending.delete(id); reject(new Error('A művelet nem válaszolt. Próbáld újra.')); }, 180000);
      pending.set(id, { resolve, reject, timeout });
      window.chrome.webview.postMessage({ id, command, data });
    });
  }
  window.chrome.webview.addEventListener('message', event => {
    const message = event.data;
    if(message.command==='saveCommentsBeforeClose'){flushSectionComments().catch(ex=>error(ex.message));return;}
    if(message.command==='autoRefresh'){autoRefresh();return;}
    const call = pending.get(message.id);
    if (!call) return;
    pending.delete(message.id); clearTimeout(call.timeout);
    message.ok ? call.resolve(message.result) : call.reject(new Error(message.error));
  });
  function icons() {
    root.querySelectorAll('button').forEach(b => b.type = 'button');
    if (window.lucide) window.lucide.createIcons({attrs:{width:16,height:16}});
    window.i18n.apply(root, state?.settings.language || 'hu');
  }
  function notify(message) {
    const toast = root.querySelector('[data-toast]');
    toast.textContent = window.i18n.text(message,state.settings.language); toast.hidden = false;
    clearTimeout(toastTimer); toastTimer = setTimeout(() => toast.hidden = true, 4500);
  }
  function error(message) {
    const target = root.querySelector('[data-dialog-error]') || root.querySelector('[data-error]');
    target.hidden = false; target.textContent = window.i18n.text(message,state?.settings.language || 'hu');
  }
  function autoRefresh() {
    // Silent background refresh: it never interrupts editing, dialogs, previews, a selection or another operation.
    const active=document.activeElement;
    if(busy||autoRun||draft||plan||commentDrafts.size||savingComments||(active&&active.matches('textarea,input,select'))
      ||String(window.getSelection?.()||'').length||Date.now()-lastAutoRefresh<15000)return;
    lastAutoRefresh=Date.now();
    autoRun=rpc('refresh').then(result=>{if(!busy&&!draft&&!plan){state=result;render();}}).catch(()=>{}).finally(()=>{autoRun=null;});
  }
  async function perform(action, success = '') {
    if (busy) return;
    busy = true; root.querySelector('[data-busy]').hidden = false;
    try { if (autoRun) await autoRun; await flushSectionComments(); await action(); if (success) notify(success); }
    catch (ex) { error(ex.message); }
    finally { busy = false; root.querySelector('[data-busy]').hidden = true; }
  }
  function dayCard(day) {
    const open = expanded.has(day.key);
    const firstLabel = day.firstSystem ? day.firstSystem : day.firstFromSignIn ? (day.incomplete?'Első észlelt bejelentkezés':'Első bejelentkezés') : day.firstFromSession ? (day.incomplete?'Első észlelt kapcsolódás':'Első kapcsolódás') : (day.incomplete?'Első észlelt feloldás':'Első feloldás');
    const lastLabel = day.lastFromActivity?'Utolsó aktivitás':day.lastSystem?day.lastSystem:day.lastFromSignOut?(day.ongoing?'Legutóbbi kijelentkezés':'Kijelentkezés / leállítás'):day.lastFromSession?(day.ongoing?'Legutóbbi leválasztás':'Utolsó leválasztás'):day.ongoing?'Legutóbbi zárolás':'Utolsó zárolás';
    return `<article class="cu-day" data-day="${day.key}"><div class="cu-row cu-between cu-day-head"><div class="cu-row"><button class="cu-quiet" data-expand="${day.key}" aria-expanded="${open}" aria-label="${day.date} eseményei">${icon(open?'chevron-down':'chevron-right')}</button><span class="cu-date">${field(day.date,'Dátum')}${copyButton(day.date,'Dátum')}</span><span class="cu-sub">${esc(day.weekday)}</span><span class="cu-pill ${day.incomplete?'cu-warn':day.ongoing?'cu-good':''}">${icon(day.incomplete?'triangle-alert':day.ongoing?'clock-3':'check')}${esc(day.status)}</span></div><button class="cu-quiet" data-copy="${esc(day.copy)}" ${day.copy?'':'disabled'}>${icon('copy')}Munkaszakaszok másolása</button></div><div class="cu-metrics"><div><span class="cu-label">${firstLabel}</span><div class="cu-metric">${time(day.first,firstLabel,true,day.date)}</div></div><div><span class="cu-label">Ebédszünet</span>${day.lunch?`<div class="cu-metric-row"><div class="cu-metric">${time(day.lunch.from,'Ebéd kezdete',true,day.date)} – ${time(day.lunch.to,'Ebéd vége',true,day.date)}</div><span class="cu-pill ${day.manual?'cu-good':''}">${icon(day.manual?'check':'sparkles')}${day.manual?'Kézi választás':'Becsült'} · ${day.lunch.minutes} perc</span></div>`:`<div>${day.none?'Nem volt ebéd':'Nincs megfelelő pár'}</div><span class="cu-sub">A nap eseményeinél kiválasztható</span>`}</div><div><span class="cu-label">${lastLabel}</span><div class="cu-metric-row"><div class="cu-metric">${time(day.last,lastLabel,true,day.date)}</div>${day.ongoing?'<span class="cu-sub">A mai nap még folyamatban</span>':''}</div>${state.settings.lockTimeSource==='activity'?`<span class="cu-sub">${day.lastFromActivity?'Aktivitás alapján':day.lastSystem?'Nincs aktivitási adat · rendszeresemény alapján':day.lastFromSignOut?'Nincs aktivitási adat · kijelentkezés alapján':day.lastFromSession?'Nincs aktivitási adat · leválasztás alapján':'Nincs aktivitási adat · zárolás alapján'}</span>`:''}</div></div>${meetingSummary(day)}${day.issues.length?`<div class="cu-error">${day.issues.map(esc).join('<br>')}</div>`:''}<div class="cu-work"><span class="cu-work-title">${icon('sheet')} Munkaszakaszok:</span>${day.sections.map(s=>`<span class="cu-work-chip">${time(s.from,'Kezdés',false,day.date)} – ${s.open?'folyamatban':time(s.to,'Befejezés',false,day.date)}${s.open?'<button class="cu-quiet cu-work-copy" disabled aria-label="A szakasz még folyamatban">'+icon('copy')+'</button>':copyButton(s.copy,'Munkaszakasz','cu-work-copy')}</span>`).join('')}<span class="cu-sub">${day.incomplete?'Automatikus kitöltésből kihagyva':day.ongoing?'Teljes napként még nem exportálható':day.sections.length+' sor'}</span></div>${open?dayDetails(day):''}</article>`;
  }
  function activityDetail(value, date) {
    return value ? `<span class="cu-activity-value">${time(value.lastInput,'Utolsó aktivitás',true,date)}<span class="cu-sub">Inaktivitás: ${field(value.idle,'Inaktivitás zárolás előtt')}</span></span>` : '<span class="cu-sub">Nincs aktivitási adat</span>';
  }
  const reasonNames=[['standup','Standup meeting'],['lunch','Ebédszünet'],['coffee','Kávészünet'],['meeting','Meeting'],['other','Egyéb szünet']];
  function reasonCell(p) {
    // Standup and meeting count as work; the source tells whether the answer came from the prompt, its timeout or a manual choice.
    const source=p.reasonManual?'Kézi választás':p.reasonAutomatic?'Automatikus (lejárt kérdés)':p.reason?'Felugró ablakból':'';
    return `<td class="cu-reason-cell"><div class="cu-reason-row"><select data-break-reason="${esc(p.id)}" aria-label="Szünet oka"><option value="" ${p.reason?'':'selected'}>—</option>${reasonNames.map(([v,n])=>`<option value="${v}" ${p.reason===v?'selected':''}>${n}</option>`).join('')}</select>${p.reasonManual?`<button class="cu-quiet" data-break-reset="${esc(p.id)}" aria-label="Kézi választás törlése" title="Kézi választás törlése">${icon('undo-2')}</button>`:''}</div>${p.reason==='other'?`<input class="cu-reason-text" data-break-text="${esc(p.id)}" maxlength="200" value="${esc(p.reasonText||'')}" placeholder="Leírás" aria-label="Szünet leírása">`:''}${source?`<span class="cu-sub">${source}${p.reason==='standup'||p.reason==='meeting'?' · munkaidő':''}</span>`:''}</td>`;
  }
  function activityStatus() {
    const a=state.activity||{enabled:false,running:false,registered:false};
    // Only the non-default time source needs a reminder; lock times are the normal case.
    return `<span class="cu-activity-status"><span>${icon('mouse-pointer-2')} ${!a.enabled&&!a.prompts?'Háttérfigyelő kikapcsolva':a.running?'Háttérfigyelő működik':'Háttérfigyelő nem válaszol'}</span>${state.settings.lockTimeSource==='activity'?'<span class="cu-sub">Munkaszakaszok: utolsó aktivitás, adat hiányában zárolás</span>':''}${(a.enabled||a.prompts)&&!a.running?'<button class="cu-quiet" data-start-activity>Újraindítás</button>':''}${a.error?`<span class="cu-error">${esc(a.error)}</span>`:''}</span>`;
  }
  function meetingSummary(day) {
    if (!state.settings.ignoreDailyMeeting) return '';
    return `<div class="cu-meeting-summary" data-meeting-summary><strong>${icon('users')} Napi meeting:</strong>${day.meeting?`<span>${time(day.meeting.from,'Meeting kezdete',true,day.date)} – ${time(day.meeting.to,'Meeting vége',true,day.date)}</span><span class="cu-pill">${day.manualMeeting?'Kézi választás':'Becsült'}</span><span class="cu-sub">Munkaidőnek számít</span>`:`<span class="cu-sub">${day.noMeeting?'Nem volt napi meeting':'Nincs megfelelő meetingpár'}</span>`}</div>`;
  }
  function sectionRows(day) {
    return `<details class="cu-section-details" data-section-details="${day.key}" ${closedSections.has(day.key)?'':'open'}><summary>Munkaszakaszok (${day.sections.length})</summary><p class="cu-note">A sor másolása csak a dátumot, feloldást és zárolást írja az Excel A–C oszlopaiba. A hossz és óraszám képletei megmaradnak; a megjegyzés külön másolható.</p><div class="cu-table-wrap"><table class="cu-sections-table"><thead><tr><th></th><th>Dátum</th><th>Feloldás</th><th>Zárolás</th><th>Hossz</th><th>Megjegyzés</th></tr></thead><tbody>${day.sections.map(s=>`<tr data-section-row="${esc(s.id)}"><td><button class="cu-quiet" data-section-copy="${esc(s.id)}" ${s.open?'disabled':''} aria-label="Időadatok másolása (Excel A–C)" title="Időadatok másolása (Excel A–C)">${icon('copy')}</button></td><td><span class="cu-exact">${field(s.date,'Dátum')}${copyButton(s.date,'Dátum')}</span></td><td>${time(s.from,'Feloldás',true,day.date)}</td><td>${s.open?'<span class="cu-sub">folyamatban</span>':time(s.to,'Zárolás',true,day.date)}</td><td><span class="cu-exact">${field(s.duration,'Időtartam')}${copyButton(s.durationExact||s.duration,'Időtartam')}</span>${s.open?'<span class="cu-sub">eddig</span>':''}</td><td class="cu-comment-cell"><div class="cu-comment-editor"><textarea rows="1" maxlength="32767" data-section-comment="${esc(s.id)}" aria-label="Munkaszakasz megjegyzése" placeholder="Megjegyzés a Details oszlophoz">${esc(commentDrafts.get(s.id)??s.comment??'')}</textarea><div class="cu-comment-actions"><button class="cu-quiet" data-comment-copy="${esc(s.id)}" aria-label="Megjegyzés másolása">${icon('copy')}</button><span class="cu-sub" data-comment-status="${esc(s.id)}">${commentDrafts.has(s.id)?'Mentés...':'Mentve'}</span></div></div></td></tr>`).join('')}</tbody></table></div></details>`;
  }
  function findSection(id) { return state.days.flatMap(day=>day.sections).find(s=>s.id===id); }
  function commentStatus(id,text) {
    root.querySelectorAll('[data-comment-status]').forEach(node=>{if(node.dataset.commentStatus===id)node.textContent=window.i18n.text(text,state.settings.language);});
  }
  async function flushSectionComments() {
    while(commentDrafts.size||savingComments){
      if(savingComments){await savingComments;continue;}
      savingComments=(async()=>{
        await rpc('commentsPending',{pending:true});
        while(commentDrafts.size){
          const [sectionId,comment]=commentDrafts.entries().next().value;
          try { await rpc('sectionComment',{sectionId,comment}); }
          catch(ex){commentStatus(sectionId,'A mentés nem sikerült');await rpc('commentsFailed').catch(()=>{});throw ex;}
          if(commentDrafts.get(sectionId)===comment){commentDrafts.delete(sectionId);commentStatus(sectionId,'Mentve');}
        }
        await rpc('commentsPending',{pending:false});
      })();
      try { await savingComments; }
      finally { savingComments=null; }
    }
  }
  function dayDetails(day) {
    const meetingEnabled=state.settings.ignoreDailyMeeting;
    return `<section class="cu-detail"><details class="cu-pair-details" data-pair-details="${day.key}" ${pairDetails.has(day.key)?'open':''}><summary>Lock / unlock párok (${day.pairs.length})</summary>
      ${day.hasSessionEvents?`<p class="cu-sub">A kapcsolódás munkakezdésnek, a leválasztás szünetkezdetnek számít. Az egyidejű lock/unlock csak egyszer számít.</p>`:''}
      ${day.hasSignInEvents?`<p class="cu-sub">A bejelentkezés munkakezdésnek, a kijelentkezés vagy leállítás szünetkezdetnek számít. Újraindítás után az új munkamenet ugyanazt a napot folytatja.</p>`:''}
      ${day.hasSystemEvents?`<p class="cu-sub">Az alvás, a leállítás és a váratlan leállás a Windows System naplójából származik, és csak nyitott munkamenetben számít. A váratlan leállás időpontja a rendszer utolsó életjele, ezért becsült.</p>`:''}
      <div class="cu-table-wrap"><table class="cu-pairs-table"><thead><tr><th>${state.settings.lockTimeSource==='activity'?'Szakasz vége':'Zárolás'}</th><th>Feloldás</th><th>Szünet</th><th>Ok</th><th>Utolsó aktivitás zárolás előtt</th><th>Ebéd</th>${meetingEnabled?'<th>Napi meeting</th>':''}</tr></thead><tbody>${day.pairs.map(p=>`<tr class="${day.lunch?.id===p.id?'cu-chosen':day.meeting?.id===p.id?'cu-meeting-chosen':''}"><td>${time(p.from,p.fromActivity?'Utolsó aktivitás':'Zárolás',false,day.date)}${state.settings.lockTimeSource==='activity'?`<span class="cu-sub">${p.fromActivity?'Aktivitás alapján':'Zárolás alapján'}</span>`:''}</td><td>${time(p.to,'Feloldás',false,day.date)}</td><td>${p.minutes} perc</td>${reasonCell(p)}<td>${activityDetail(p.activity,day.date)}</td><td><label class="cu-check"><input type="radio" name="lunch-${day.key}" data-lunch="${day.key}" value="${esc(p.id)}" ${day.lunch?.id===p.id?'checked':''}>${day.lunch?.id===p.id?'Ebéd':'Kijelölés'}</label></td>${meetingEnabled?`<td><label class="cu-check"><input type="radio" name="meeting-${day.key}" data-meeting="${day.key}" value="${esc(p.id)}" ${day.meeting?.id===p.id?'checked':''}>${day.meeting?.id===p.id?'Meeting':'Kijelölés'}</label></td>`:''}</tr>`).join('')}</tbody></table></div>
      <div class="cu-row cu-between cu-lunch-actions"><label class="cu-check"><input type="radio" name="lunch-${day.key}" data-lunch="${day.key}" value="none" ${day.none?'checked':''}>Nem volt ebéd</label><div class="cu-row">${day.manual?`<button class="cu-quiet" data-auto="${day.key}">${icon('sparkles')}Becslés használata</button>`:''}${day.undo?`<button data-undo="${day.key}">${icon('undo-2')}Visszavonás</button>`:''}</div></div>
      ${meetingEnabled?`<div class="cu-row cu-between cu-lunch-actions"><label class="cu-check"><input type="radio" name="meeting-${day.key}" data-meeting="${day.key}" value="none" ${day.noMeeting?'checked':''}>Nem volt napi meeting</label>${day.manualMeeting?`<button class="cu-quiet" data-meeting-auto="${day.key}">${icon('sparkles')}Meeting becslése</button>`:''}</div>`:''}</details>
      ${sectionRows(day)}
      <details style="margin-top:12px"><summary>Összes naplóesemény (${day.events.length})</summary><div class="cu-row cu-detail-top"><button class="cu-quiet" data-copy="${esc(day.eventCopy)}">${icon('copy')}Napló másolása</button></div><div class="cu-table-wrap"><table><thead><tr><th>Dátum</th><th>Időpont</th><th>Esemény</th><th>Azonosító</th><th>Utolsó aktivitás zárolás előtt</th></tr></thead><tbody>${day.events.map(e=>`<tr><td>${field(e.time.date,'Esemény dátuma')}</td><td>${time(e.time,'Esemény időpontja')}</td><td>${esc(e.label)}</td><td>${e.eventId}</td><td>${(e.isLock??e.eventId===4800)?activityDetail(e.activity,day.date):'—'}</td></tr>`).join('')}</tbody></table></div></details></section>`;
  }
  function accessWarning() {
    if(!state.permissions?.readAccessRequired)return '';
    return `<section class="cu-logging-warning" data-read-access-warning role="alert"><button data-settings="permissions">${icon('shield-alert')}<span class="cu-warning-text"><strong>A Windows Security napló olvasásához nincs jogosultság.</strong><span class="cu-warning-body">A mentett adatok látszanak, de a napló nem frissült. Az egyszeri olvasási beállítás után a frissítés nem kér rendszergazdai jogot.</span><span class="cu-warning-action">Olvasási jogosultság beállítása</span></span>${icon('chevron-right')}</button></section>`;
  }
  function loggingWarning() {
    if (state.logging !== false) return '';
    return `<section class="cu-logging-warning" data-logging-warning role="alert"><button data-settings="logging">${icon('triangle-alert')}<span class="cu-warning-text"><strong>A Windows lock/unlock naplózás ki van kapcsolva.</strong><span class="cu-warning-body">Naplózás nélkül az új zárolások és feloldások nem kerülnek rögzítésre, ezért a munkaidőnapló nem tudja követni a munkaszakaszokat.</span><span class="cu-warning-action">Bekapcsolás a beállításokban</span></span>${icon('chevron-right')}</button></section>`;
  }
  function render() {
    const y = window.scrollY, file = state.settings.workbookPath.split(/[\\/]/).pop(), w = state.workbook || {};
    root.style.setProperty('--cu-scale', state.settings.fontScale);
    document.documentElement.style.colorScheme = state.settings.theme === 'auto' ? 'light dark' : state.settings.theme;
    root.innerHTML = `<div data-background><div class="cu-titlebar"><img class="cu-app-icon" src="assets/app-icon.png" alt=""><strong>CompUsage</strong><span class="cu-muted">Saját Windows-fiók · ${esc(state.user)}</span></div><main class="cu-shell"><header class="cu-row cu-between"><div class="cu-head-line"><h1>Munkaidőnapló</h1><span class="cu-sub cu-live">${icon(state.logging?'shield-check':'shield-alert')}Naplózás ${state.logging===null?'állapota ismeretlen':state.logging?'bekapcsolva':'kikapcsolva'}</span>${activityStatus()}</div><button data-settings>${icon('settings-2')}Beállítások</button></header>${loggingWarning()}${accessWarning()}<div class="cu-row cu-between cu-toolbar"><div class="cu-row"><div class="cu-segmented" aria-label="Előzmények">${[7,30,60].map(n=>`<button data-history="${n}" aria-pressed="${state.settings.historyDays===n}">${n} nap</button>`).join('')}</div><button class="cu-quiet" data-refresh>${icon('refresh-cw')}Frissítés</button></div><button data-copy-all>${icon('copy')}Összesítő másolása</button></div><div class="cu-error" role="alert" data-error ${state.error?'':'hidden'}>${esc(state.error)}</div><section class="cu-file"><div class="cu-row cu-between"><div><div class="cu-row cu-file-name">${icon('file-spreadsheet')}${esc(file||'Nincs beállítva munkaidőfájl')}</div>${w.available?`<p class="cu-sub">Details utolsó dátuma: ${w.lastDate?field(w.lastDate,'Utolsó dátum'):'még üres'}</p><div class="cu-pending">${w.days} új nap · ${w.rows} új sor${w.partials?' · '+w.partials+' megkezdett sor':''}</div>${w.rows||w.partials?'':'<p class="cu-sub" data-append-empty>Nincs hozzáfűzhető lezárt nap a Details utolsó dátuma után. A folyamatban lévő nap a munkanap vége után kerülhet be.</p>'}`:`<p class="cu-sub">${esc(w.error||'A beállításokban válassz .xlsx fájlt.')}</p>`}</div><div class="cu-row"><button data-open-file ${w.available?'':'disabled'}>${icon('external-link')}Megnyitás</button><button class="cu-primary" data-append ${w.available&&(w.rows||w.partials)?'':'disabled'}>${icon('list-plus')}Details folytatása</button></div></div></section><div data-import>${plan?importMarkup():''}</div><div class="cu-row cu-between cu-section"><span>${state.days.length} munkanap</span><span>${state.refreshed?'Frissítve: '+esc(state.refreshed):'Még nem történt naplóolvasás'}</span></div>${state.days.length?state.days.map(dayCard).join(''):`<div class="cu-empty">Nincs lock/unlock esemény a kiválasztott időszakban.<br>A Frissítés beolvassa az elérhető Windows-eseményeket.</div>`}</main></div><div data-modal></div><div data-toast role="status" hidden></div><div class="cu-busy" data-busy ${busy?'':'hidden'}></div>`;
    root.querySelectorAll('[data-section-comment]').forEach(t=>{t.value=commentDrafts.get(t.dataset.sectionComment)??findSection(t.dataset.sectionComment)?.comment??'';});
    icons(); window.scrollTo(0,y); if(draft)renderSettings();
  }
  function importMarkup() {
    // A still-open section is written without an end; the next append completes it as a started row.
    const end=s=>s.open?'<span class="cu-sub">üresen marad (folyamatban)</span>':time(s.to,'Befejezés');
    const newRows=list=>list.map(s=>`<p data-partial-following>${time(s.from,'Kezdés')} → ${end(s)}</p>`).join('');
    return `<section class="cu-import"><div class="cu-row cu-between"><h2>Details folytatása</h2><button class="cu-quiet" data-close-plan aria-label="Előnézet bezárása">${icon('x')}</button></div><p class="cu-note">${plan.rows.length} új sor · a teljes napokból és a mai napból. A folyamatban lévő szakasz vége üresen marad. A megjegyzések a Details oszlopba kerülnek.</p><div class="cu-table-wrap"><table><thead><tr><th>Date</th><th>From</th><th>To</th><th>Duration</th><th>Details</th></tr></thead><tbody>${plan.rows.map(s=>`<tr><td>${field(s.date,'Dátum')}</td><td>${time(s.from,'Kezdés')}</td><td>${end(s)}</td><td>${s.open?'—':field(s.duration,'Időtartam')}</td><td class="cu-comment-preview" data-user-text>${esc(s.comment||'')}</td></tr>`).join('')}</tbody></table></div>${plan.partials.map(p=>p.completesRow===false?`<div class="cu-resume"><label class="cu-check"><input type="checkbox" data-partial="${p.row}">A nap folytatása (${p.row}. sor után)</label><p>${field(p.section.date,'Dátum')}</p>${newRows([p.section,...(p.following||[])])}</div>`:`<div class="cu-resume"><label class="cu-check"><input type="checkbox" data-partial="${p.row}">Megkezdett sor befejezése (${p.row}. sor)</label><p>${field(p.section.date,'Dátum')} · ${time(p.section.from,'Kezdés')} → ${time(p.section.to,'Befejezés')}</p><p class="cu-comment-preview" data-user-text>${esc(p.section.comment||'')}</p>${p.keepsExistingComment?'<p class="cu-note">A meglévő Excel-megjegyzés megmarad.</p>':''}${(p.following||[]).length?`<p class="cu-sub">Utána új sorként:</p>${newRows(p.following)}`:''}<span class="cu-sub">A beírt kezdés legfeljebb 3 perccel térhet el a rögzített szakasz kezdetétől. A nap a legutolsó elérhető adatig folytatódik.</span></div>`).join('')}<p class="cu-note">Pontos másodpercek · mentés előtt biztonsági másolat. Az Excelben előbb zárd be ezt a fájlt.</p><div class="cu-row cu-between"><button data-close-plan>Mégse</button><button class="cu-primary" data-commit ${plan.rows.length?'':'disabled'}>${icon('save')}Mentés a Details lapra</button></div></section>`;
  }
  function settingField(key,label,type='text',extra='') { return `<label class="cu-field"><span>${label}</span><input data-setting="${key}" type="${type}" value="${esc(draft.settings[key])}" ${extra}></label>`; }
  function renderSettings() {
    root.querySelector('[data-background]').inert = true;
    const s=draft.settings;document.documentElement.style.colorScheme=s.theme==='auto'?'light dark':s.theme;
    const appearanceFields=`<div class="cu-fields" style="margin-bottom:16px"><label class="cu-field"><span>Nyelv</span><select data-setting="language"><option value="hu" ${s.language==='hu'?'selected':''}>Magyar</option><option value="en" ${s.language==='en'?'selected':''}>Angol</option></select></label><label class="cu-field"><span>Téma</span><select data-setting="theme">${[['auto','OS követése'],['dark','Sötét'],['light','Világos']].map(([key,label])=>`<option value="${key}" ${s.theme===key?'selected':''}>${label}</option>`).join('')}</select></label></div>`;
    root.querySelector('[data-modal]').innerHTML=`<div class="cu-overlay"><section class="cu-dialog" role="dialog" aria-modal="true" aria-labelledby="settings-title"><div class="cu-row cu-between"><h2 id="settings-title">Beállítások</h2><button class="cu-quiet" data-cancel aria-label="Bezárás">${icon('x')}</button></div><div class="cu-tabs" role="tablist">${[['time','Időszámítás'],['meeting','Napi meeting'],['breaks','Szünetek'],['excel','Excel'],['display','Megjelenítés']].map(([k,n])=>`<button role="tab" data-tab="${k}" id="tab-${k}" aria-controls="panel-${k}" aria-selected="${tab===k}">${n}</button>`).join('')}</div><div role="tabpanel" id="panel-time" aria-labelledby="tab-time" ${tab==='time'?'':'hidden'}><div class="cu-read-access"><button data-grant-read-access ${!state.permissions?.readAccessRequired&&state.logging!==null?'disabled':''}>${icon('shield-check')}Olvasási jogosultság beállítása</button><p class="cu-note">Egyszeri Windows-jóváhagyás: a fiókod olvasási jogot kap a teljes Security naplóhoz és az auditállapothoz. Naplótörlési vagy auditmódosítási jogot nem kap. A CompUsage csak a saját lock/unlock eseményeidet dolgozza fel.</p></div><label class="cu-check"><input type="checkbox" data-logging ${draft.logging?'checked':''}>Windows lock/unlock naplózás</label><p class="cu-note">A Windows naplózása az alkalmazás bezárása után is működik. Átállításához Windows-jóváhagyás szükséges lehet.</p><label class="cu-check"><input type="checkbox" data-activity-enabled ${draft.activityEnabled?'checked':''}>Háttérfigyelő engedélyezése</label><p class="cu-note">Alapból kikapcsolva. Csak az utolsó input időpontját tárolja, billentyűket és szöveget nem. Bekapcsolva az ablak bezárása után is fut.</p><p class="cu-note">${state.activity?.registered?'Automatikus indulás Windows-bejelentkezéskor beállítva.':'Automatikus induláshoz futtasd a telepítőt.'}</p><label class="cu-field"><span>Munkaszakaszok időforrása</span><select data-setting="lockTimeSource"><option value="lock" ${s.lockTimeSource!=='activity'?'selected':''}>Windows-zárolás időpontja</option><option value="activity" ${s.lockTimeSource==='activity'?'selected':''}>Utolsó aktivitás időpontja</option></select></label><p class="cu-note">A kijelzést, másolást és Excel-kitöltést is módosítja. Adat hiányában a zárolási idő marad. A mérés kb. 1 másodperces; a zárolás okát nem bizonyítja.</p><div class="cu-fields">${settingField('dayStart','Új munkanap kezdete','text','maxlength="5" placeholder="04:00"')}${settingField('expectedLunch','Várható ebédidő','text','maxlength="5" placeholder="11:30"')}${settingField('minimumLunchMinutes','Ebédminimum (perc)','number','min="1" max="120"')}${settingField('splitBreakMinutes','Sorokra bontó szünet (perc)','number','min="1" max="480"')}</div><p class="cu-note">Csak a határnál hosszabb szünet marad ki. A kiválasztott ebéd mindig kimarad.</p></div><div role="tabpanel" id="panel-meeting" aria-labelledby="tab-meeting" ${tab==='meeting'?'':'hidden'}><label class="cu-check"><input type="checkbox" data-setting="ignoreDailyMeeting" ${s.ignoreDailyMeeting?'checked':''}>Napi meeting figyelmen kívül hagyása</label><p class="cu-note">Alapból kikapcsolva. A kijelölt napi meeting munkaidőnek számít, és nem bontja szét a munkaszakaszt.</p><div class="cu-fields">${settingField('meetingStartFrom','Meeting kezdése ettől','text','maxlength="5" placeholder="09:25"')}${settingField('meetingStartUntil','Meeting kezdése eddig','text','maxlength="5" placeholder="09:40"')}${settingField('maximumMeetingMinutes','Meeting maximum (perc)','number','min="1" max="120"')}</div><p class="cu-note">Az automatikus becslés a kezdési idősávon belüli, legfeljebb a megadott hosszúságú párt választja. Több jelölt esetén az idősáv közepéhez legközelebbi kezdés nyer.</p><p class="cu-note">A napi listában kézzel másik párt is kijelölhetsz. Az ebéd és a meeting nem lehet ugyanaz a pár. A kiválasztott ebéd mindig kimarad a munkaidőből.</p></div><div role="tabpanel" id="panel-breaks" aria-labelledby="tab-breaks" ${tab==='breaks'?'':'hidden'}><label class="cu-check"><input type="checkbox" data-setting="breakPrompt" ${s.breakPrompt?'checked':''}>Szünet okának megkérdezése</label><p class="cu-note">Alapból bekapcsolva. Feloldás vagy bejelentkezés után a képernyő jobb alsó sarkában megkérdezi az előző szünet okát, ha az hosszabb volt a sorokra bontó szünetnél (${esc(s.splitBreakMinutes)} perc). A nap első feloldásakor nem kérdez. Választáskor azonnal ment és bezárul.</p><div class="cu-fields">${settingField('breakPromptSeconds','Kérdés bezárása ennyi idő után (mp)','number','min="5" max="3600"')}${settingField('coffeeBreakMaxMinutes','Kávészünet maximum (perc)','number','min="1" max="480"')}</div><p class="cu-note">A visszaszámlálás szünetel, amíg az egér az ablakon van vagy az ablakban dolgozol. Lejáratkor csak a javasolt standup vagy ebédszünet mentődik; más javaslat nem.</p><p class="cu-note">Javaslat: standup a napi meeting idősávjában és maximális hosszán belül; ebéd a várható ebédidő körül; ennél rövidebb vagy egyenlő szünet kávészünet, hosszabb meeting. A standup és a meeting munkaidőnek számít, és nem bontja a munkaszakaszt; a kávé- és az egyéb szünet igen. A választás a napi listában módosítható.</p><p class="cu-note">A kérdést a háttérfolyamat jeleníti meg, amely a tálcán ikont is mutat. Ha a háttérfigyelő ki van kapcsolva, inputidőt nem mér.</p></div><div role="tabpanel" id="panel-excel" aria-labelledby="tab-excel" ${tab==='excel'?'':'hidden'}><label class="cu-field"><span>Munkaidőfájl (.xlsx)</span><div class="cu-path-row"><input data-setting="workbookPath" value="${esc(s.workbookPath)}"><button data-pick>${icon('folder-open')}Tallózás</button></div></label><p class="cu-note">Details munkalap · kitöltés előtt előnézet és biztonsági másolat.</p></div><div role="tabpanel" id="panel-display" aria-labelledby="tab-display" ${tab==='display'?'':'hidden'}>${appearanceFields}<label class="cu-field"><span>Betűméret</span><select data-setting="fontScale">${[[1,'Normál'],[1.15,'Nagy'],[1.3,'Extra nagy']].map(([v,n])=>`<option value="${v}" ${Number(s.fontScale)===v?'selected':''}>${n}</option>`).join('')}</select></label><div class="cu-preview" data-font-preview style="font-size:${s.fontScale/state.settings.fontScale}em"><span class="cu-label">Első feloldás</span><div class="cu-metric">${field('08:02','Betűméret mintája')}</div></div><label class="cu-check" style="margin-top:16px"><input type="checkbox" data-setting="rememberView" ${s.rememberView?'checked':''}>Nézet megjegyzése</label><p class="cu-note">Betűméret, ablakméret, nyitott napok és görgetési helyzet.</p></div><div role="alert" data-dialog-error></div><div class="cu-dialog-bottom"><button data-cancel>Mégse</button><button class="cu-primary" data-save>${icon('check')}Mentés</button></div></section></div>`;
    icons();root.querySelector('[data-logging]').indeterminate=draft.logging===null;
  }
  function closeSettings(restoreTheme = true) { if(restoreTheme)rpc('previewTheme',{theme:state.settings.theme}).catch(ex=>error(ex.message)); document.documentElement.style.colorScheme=state.settings.theme==='auto'?'light dark':state.settings.theme; draft=null; root.querySelector('[data-modal]').innerHTML='';root.querySelector('[data-background]').inert=false;root.querySelector('[data-settings]').focus({preventScroll:true}); }
  async function update(command,data) { const result=await rpc(command,data);state=result;plan=null;render(); }
  async function copy(text) { await rpc('copy',{text});notify('Másolva'); }
  function selected(target) { return ['INPUT','TEXTAREA'].includes(target.tagName)&&typeof target.selectionStart==='number'?target.value.slice(target.selectionStart,target.selectionEnd):window.getSelection()?.toString()||''; }
  function saveView() { if(started&&state.settings.rememberView)rpc('view',{expanded:[...expanded],scroll:window.scrollY}).catch(()=>{}); }
  root.addEventListener('focusin',e=>{if(e.target.matches('.cu-value'))e.target.select();});
  root.addEventListener('click',e=>{
    if(e.target.matches('.cu-value')){if(e.target.selectionStart===e.target.selectionEnd)e.target.select();return;}
    const button=e.target.closest('button');if(!button||button.disabled||busy)return;
    const d=button.dataset;
    if('sectionCopy'in d){perform(()=>{const s=findSection(d.sectionCopy);if(!s||s.open)return;return copy(s.copy);});return;}
    if('commentCopy'in d){perform(()=>copy(findSection(d.commentCopy)?.comment||''));return;}
    if('copy'in d){perform(()=>copy(d.copy));return;}
    if('expand'in d){expanded.has(d.expand)?expanded.delete(d.expand):expanded.add(d.expand);render();root.querySelector(`[data-expand="${d.expand}"]`).focus({preventScroll:true});saveView();return;}
    if('settings'in d){draft={settings:{...state.settings},logging:state.logging,activityEnabled:state.activity?.enabled||false};draft.settings.dayStart=draft.settings.dayStart.slice(0,5);draft.settings.expectedLunch=draft.settings.expectedLunch.slice(0,5);draft.settings.meetingStartFrom=(draft.settings.meetingStartFrom||'09:25').slice(0,5);draft.settings.meetingStartUntil=(draft.settings.meetingStartUntil||'09:40').slice(0,5);draft.settings.maximumMeetingMinutes??=20;draft.settings.breakPrompt??=true;draft.settings.breakPromptSeconds??=40;draft.settings.coffeeBreakMaxMinutes??=25;tab='time';renderSettings();root.querySelector(d.settings==='permissions'?'[data-grant-read-access]':d.settings==='logging'?'[data-logging]':'[data-tab="time"]').focus();return;}
    if('cancel'in d){closeSettings();return;}
    if('tab'in d){tab=d.tab;renderSettings();root.querySelector(`[data-tab="${tab}"]`).focus();return;}
    if('closePlan'in d){plan=null;root.querySelector('[data-import]').innerHTML='';return;}
    perform(async()=>{
      if('grantReadAccess'in d){state=await rpc('grantReadAccess');if(draft.logging===null)draft.logging=state.logging;plan=null;render();notify(state.permissions?.readAccessRequired?'A napló továbbra sem olvasható.':'Olvasási jogosultság beállítva');}
      if('startActivity'in d)await update('startActivity');
      if('refresh'in d){await update('refresh');if(!state.error&&!state.permissions?.readAccessRequired)notify('Napló frissítve');}
      if('history'in d)await update('history',{days:Number(d.history)});
      if('openFile'in d)await rpc('openFile');
      if('pick'in d){const path=await rpc('pickFile');if(path){draft.settings.workbookPath=path;renderSettings();}}
      if('save'in d){if(['dayStart','expectedLunch','meetingStartFrom','meetingStartUntil'].some(k=>!/^([01]\d|2[0-3]):[0-5]\d$/.test(draft.settings[k])))throw new Error('Az időpont formátuma ÓÓ:PP legyen.');const result=await rpc('settings',{...draft,settings:{...draft.settings,dayStart:draft.settings.dayStart+':00',expectedLunch:draft.settings.expectedLunch+':00',meetingStartFrom:draft.settings.meetingStartFrom+':00',meetingStartUntil:draft.settings.meetingStartUntil+':00'}});closeSettings(false);state=result;plan=null;render();notify('Beállítások elmentve');}
      if('auto'in d)await update('lunch',{date:d.auto,value:null});
      if('meetingAuto'in d)await update('meeting',{date:d.meetingAuto,value:null});
      if('breakReset'in d)await update('breakReason',{pairId:d.breakReset,reason:null});
      if('undo'in d)await update('undo',{date:d.undo});
      if('copyAll'in d){const rows=state.days.filter(day=>day.copy).slice().reverse().map(day=>day.copy);if(!rows.length)throw new Error('Nincs másolható, lezárt munkanap.');await copy(rows.join('\r\n'));}
      if('append'in d){plan=await rpc('preview');root.querySelector('[data-import]').innerHTML=importMarkup();icons();}
      if('commit'in d){const partials=[...root.querySelectorAll('[data-partial]:checked')].map(x=>Number(x.dataset.partial));const result=await rpc('commit',{token:plan.token,partials});state=result.state;plan=null;render();notify('Mentés kész. Biztonsági másolat: '+result.backup);}
    });
  });
  root.addEventListener('toggle',e=>{const t=e.target;
    if(t.matches('[data-section-details]')){t.open?closedSections.delete(t.dataset.sectionDetails):closedSections.add(t.dataset.sectionDetails);}
    if(t.matches('[data-pair-details]')){t.open?pairDetails.add(t.dataset.pairDetails):pairDetails.delete(t.dataset.pairDetails);}},true);
  root.addEventListener('input',e=>{const t=e.target;
    if(t.matches('[data-section-comment]')){
      const s=findSection(t.dataset.sectionComment);if(!s)return;
      s.comment=t.value;commentDrafts.set(s.id,t.value);
      plan=null;root.querySelector('[data-import]').innerHTML='';commentStatus(s.id,'Mentés...');
      flushSectionComments().catch(ex=>error(ex.message));return;
    }
    if(!draft)return;if(t.matches('[data-setting]')){const key=t.dataset.setting;draft.settings[key]=t.type==='checkbox'?t.checked:['fontScale','minimumLunchMinutes','splitBreakMinutes','maximumMeetingMinutes','breakPromptSeconds','coffeeBreakMaxMinutes'].includes(key)?Number(t.value):t.value;if(key==='fontScale')root.querySelector('[data-font-preview]').style.fontSize=`${Number(t.value)/state.settings.fontScale}em`;if(key==='theme'){document.documentElement.style.colorScheme=t.value==='auto'?'light dark':t.value;rpc('previewTheme',{theme:t.value}).catch(ex=>error(ex.message));}}if(t.matches('[data-logging]'))draft.logging=t.checked;if(t.matches('[data-activity-enabled]'))draft.activityEnabled=t.checked;});
  root.addEventListener('change',e=>{const t=e.target;if(t.matches('[data-lunch]')){perform(()=>update('lunch',{date:t.dataset.lunch,value:t.value}));}if(t.matches('[data-meeting]')){perform(()=>update('meeting',{date:t.dataset.meeting,value:t.value}));}if(t.matches('[data-break-reason]')){perform(()=>update('breakReason',{pairId:t.dataset.breakReason,reason:t.value||'none'}));}if(t.matches('[data-break-text]')){perform(()=>update('breakReason',{pairId:t.dataset.breakText,reason:'other',text:t.value}));}if(t.matches('[data-partial]'))root.querySelector('[data-commit]').disabled=!(plan.rows.length||root.querySelector('[data-partial]:checked'));});
  root.addEventListener('keydown',e=>{
    const t=e.target;
    if(e.ctrlKey&&!e.altKey&&(e.key==='Insert'||e.key.toLowerCase()==='c')){const text=selected(t);if(text){e.preventDefault();copy(text).catch(ex=>error(ex.message));}return;}
    if((e.shiftKey&&e.key==='Insert'||e.ctrlKey&&e.key.toLowerCase()==='v')&&t.matches('input:not([readonly]):not([type=checkbox]):not([type=radio]),textarea:not([readonly])')){
      if(typeof t.selectionStart!=='number')return;
      e.preventDefault();const start=t.selectionStart,end=t.selectionEnd;
      rpc('paste').then(text=>{t.setRangeText(text,start,end,'end');t.dispatchEvent(new Event('input',{bubbles:true}));}).catch(ex=>error(ex.message));return;
    }
    if(!draft)return;
    if(e.key==='Escape'&&!busy){e.preventDefault();closeSettings();return;}
    if(t.matches('[data-tab]')&&['ArrowLeft','ArrowRight','Home','End'].includes(e.key)){e.preventDefault();const keys=['time','meeting','breaks','excel','display'],i=keys.indexOf(tab);tab=e.key==='Home'?keys[0]:e.key==='End'?keys.at(-1):keys[(i+(e.key==='ArrowRight'?1:keys.length-1))%keys.length];renderSettings();root.querySelector(`[data-tab="${tab}"]`).focus();}
    if(e.key==='Tab'){const items=[...root.querySelector('.cu-dialog').querySelectorAll('button,input,select')].filter(x=>!x.disabled&&!x.closest('[hidden]'));if(e.shiftKey&&document.activeElement===items[0]){e.preventDefault();items.at(-1).focus();}else if(!e.shiftKey&&document.activeElement===items.at(-1)){e.preventDefault();items[0].focus();}}
  });
  window.addEventListener('scroll',()=>{clearTimeout(scrollTimer);scrollTimer=setTimeout(saveView,350);},{passive:true});
  rpc('load').then(async result=>{state=result;expanded=new Set(state.expanded);render();window.scrollTo(0,state.scroll);started=true;await perform(()=>update('refresh'));}).catch(ex=>{root.innerHTML=`<div class="cu-shell"><h1>Nem sikerült a betöltés</h1><p>${esc(ex.message)}</p></div>`;});
})();
