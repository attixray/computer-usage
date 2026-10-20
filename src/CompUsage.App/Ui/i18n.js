'use strict';
// Localization only touches UI text and accessible labels. Clipboard payloads and user values stay unchanged.
window.i18n = (() => {
  const en = {
    'Munkamenet csatlakoztatása':'Session connected','Munkamenet leválasztása':'Session disconnected',
    'Első kapcsolódás':'First connection','Első észlelt kapcsolódás':'First observed connection',
    'Utolsó leválasztás':'Last disconnect','Legutóbbi leválasztás':'Most recent disconnect',
    'Nincs aktivitási adat · leválasztás alapján':'No activity data · using disconnect time',
    'A kapcsolódás munkakezdésnek, a leválasztás szünetkezdetnek számít. Az egyidejű lock/unlock csak egyszer számít.':'Connecting starts work; disconnecting starts a break. Concurrent lock/unlock events count once.',
    'A Windows Security napló olvasásához nincs jogosultság.':'Windows Security log read access is missing.',
    'A mentett adatok látszanak, de a napló nem frissült. Az egyszeri olvasási beállítás után a frissítés nem kér rendszergazdai jogot.':'Saved data is shown, but the log was not refreshed. After one-time read-access setup, refreshing does not request administrator rights.',
    'Olvasási jogosultság beállítása':'Set up read access',
    'Egyszeri Windows-jóváhagyás: a fiókod olvasási jogot kap a teljes Security naplóhoz és az auditállapothoz. Naplótörlési vagy auditmódosítási jogot nem kap. A CompUsage csak a saját lock/unlock eseményeidet dolgozza fel.':'One-time Windows approval grants your account read access to the entire Security log and audit status. It does not grant log-clearing or audit-changing rights. CompUsage processes only your own lock/unlock events.',
    'A napló továbbra sem olvasható.':'The log still cannot be read.','Olvasási jogosultság beállítva':'Read access configured',
    'A Windows-házirend kifejezetten tiltja ezt az olvasási jogot.':'Windows policy explicitly denies this read permission.',
    'Utolsó aktivitás':'Last input','Utolsó aktivitás zárolás előtt':'Last input before lock','Szakasz vége':'Section end',
    'Inaktivitás:':'Idle time:','Inaktivitás zárolás előtt':'Idle time before lock','Nincs aktivitási adat':'No activity data',
    'Aktivitás alapján':'Based on last input','Zárolás alapján':'Based on lock time',
    'Nincs aktivitási adat · zárolás alapján':'No activity data · using lock time',
    'Háttérfigyelő kikapcsolva':'Activity monitor disabled','Háttérfigyelő működik':'Activity monitor running',
    'Háttérfigyelő nem válaszol':'Activity monitor is not responding','Újraindítás':'Restart',
    'Munkaszakaszok: utolsó aktivitás, adat hiányában zárolás':'Work sections: last input, falling back to lock time',
    'Munkaszakaszok: zárolási időpontok':'Work sections: lock times',
    'Háttérfigyelő engedélyezése':'Enable background activity monitor',
    'Alapból kikapcsolva. Csak az utolsó input időpontját tárolja, billentyűket és szöveget nem. Bekapcsolva az ablak bezárása után is fut.':'Disabled by default. Stores only the last input timestamp, never keys or text. When enabled, it keeps running after the window closes.',
    'Automatikus indulás Windows-bejelentkezéskor beállítva.':'Automatic startup at Windows sign-in is registered.',
    'Automatikus induláshoz futtasd a telepítőt.':'Run Setup to register automatic startup.',
    'Munkaszakaszok időforrása':'Work-section time source','Windows-zárolás időpontja':'Windows lock time','Utolsó aktivitás időpontja':'Last input time',
    'A kijelzést, másolást és Excel-kitöltést is módosítja. Adat hiányában a zárolási idő marad. A mérés kb. 1 másodperces; a zárolás okát nem bizonyítja.':'Affects display, copying and Excel writes. Falls back to lock time when data is unavailable. Samples about once per second; does not prove why Windows locked.',
    'Érvénytelen aktivitási időforrás.':'Invalid activity time source.',
    'Munkaidőnapló':'Work log','Saját Windows-fiók':'Current Windows account','Beállítások':'Settings',
    'Naplózás állapota ismeretlen':'Logging status unknown','Naplózás bekapcsolva':'Logging enabled','Naplózás kikapcsolva':'Logging disabled',
    'A Windows lock/unlock naplózás ki van kapcsolva.':'Windows lock/unlock logging is disabled.',
    'Naplózás nélkül az új zárolások és feloldások nem kerülnek rögzítésre, ezért a munkaidőnapló nem tudja követni a munkaszakaszokat.':'Without logging, new locks and unlocks are not recorded, so the work log cannot track work sections.',
    'Bekapcsolás a beállításokban':'Enable logging in Settings',
    'Frissítés':'Refresh','Összesítő másolása':'Copy all work sections','Nincs beállítva munkaidőfájl':'No workbook selected',
    'Details utolsó dátuma:':'Last date in Details:','még üres':'empty','Megnyitás':'Open','Details folytatása':'Append to Details',
    'Még nem történt naplóolvasás':'The event log has not been read yet','Frissítve:':'Updated:',
    'A beállításokban válassz .xlsx fájlt.':'Select an .xlsx workbook in Settings.',
    'Nincs lock/unlock esemény a kiválasztott időszakban.':'No lock/unlock events in the selected period.',
    'A Frissítés beolvassa az elérhető Windows-eseményeket.':'Refresh reads the available Windows events.',
    'az elérhető napló alapján':'based on available log data','Felületen perc · másolásikonnal és Excelben másodperc':'Minutes on screen · exact seconds via copy icons and in Excel',
    'Dátum':'Date','Első feloldás':'First unlock','Első észlelt feloldás':'First observed unlock','Utolsó zárolás':'Last lock','Legutóbbi zárolás':'Most recent lock',
    'Ebédszünet':'Lunch break','Ebéd kezdete':'Lunch start','Ebéd vége':'Lunch end','Kézi választás':'Manual selection','Becsült':'Estimated',
    'Nem volt ebéd':'No lunch break','Nincs megfelelő pár':'No matching pair','A nap eseményeinél kiválasztható':'Select a pair in the daily event list',
    'A mai nap még folyamatban':'Today is still in progress','Munkaszakaszok:':'Work sections:','Munkaszakaszok másolása':'Copy work sections',
    'Kezdés':'Start','Befejezés':'End','folyamatban':'in progress','A szakasz még folyamatban':'This section is still in progress',
    'Automatikus kitöltésből kihagyva':'Excluded from automatic append','Teljes napként még nem exportálható':'The full day cannot be exported yet',
    'Lock / unlock párok':'Lock / unlock pairs','Napló másolása':'Copy event log','Zárolás':'Lock','Feloldás':'Unlock','Szünet':'Break','Ebéd':'Lunch',
    'Kijelölés':'Select','Becslés használata':'Use estimate','Visszavonás':'Undo','Időpont':'Time','Esemény':'Event','Azonosító':'ID',
    'Esemény dátuma':'Event date','Esemény időpontja':'Event time','Időtartam':'Duration',
    'Az első megfelelő munkaszakasz végéig. Azonos dátumú további sorokhoz használd a munkaszakaszok másolását.':'Complete through the end of the matching work section. Use work-section copying for any further rows on that date.',
    'Pontos másodpercek · mentés előtt biztonsági másolat. Az Excelben előbb zárd be ezt a fájlt.':'Exact seconds · backup before saving. Close this workbook in Excel first.',
    'Mégse':'Cancel','Mentés a Details lapra':'Save to Details','Mentés':'Save','Bezárás':'Close','Előnézet bezárása':'Close preview',
    'Időszámítás':'Time rules','Excel':'Excel','Megjelenítés':'Appearance','Windows lock/unlock naplózás':'Windows lock/unlock logging',
    'A Windows naplózása az alkalmazás bezárása után is működik. Átállításához Windows-jóváhagyás szükséges lehet.':'Windows keeps logging after this app closes. Changing the setting may require Windows approval.',
    'Új munkanap kezdete':'New workday starts at','Várható ebédidő':'Expected lunch time','Ebédminimum (perc)':'Minimum lunch (minutes)',
    'Sorokra bontó szünet (perc)':'Split threshold (minutes)','Csak a határnál hosszabb szünet marad ki. A kiválasztott ebéd mindig kimarad.':'Only breaks longer than the threshold are excluded. The selected lunch break is always excluded.',
    'Munkaidőfájl (.xlsx)':'Workbook (.xlsx)','Tallózás':'Browse','Details munkalap · kitöltés előtt előnézet és biztonsági másolat.':'Details worksheet · preview and backup before writing.',
    'Betűméret':'Font size','Normál':'Normal','Nagy':'Large','Extra nagy':'Extra large','Betűméret mintája':'Font size preview',
    'Nézet megjegyzése':'Remember view','Betűméret, ablakméret, nyitott napok és görgetési helyzet.':'Font size, window bounds, expanded days and scroll position.',
    'Nyelv':'Language','Magyar':'Hungarian','Angol':'English','Téma':'Theme','OS követése':'Follow OS','Sötét':'Dark','Világos':'Light',
    'Másolva':'Copied','Napló frissítve':'Event log refreshed','Beállítások elmentve':'Settings saved','Nem sikerült a betöltés':'Unable to load',
    'Megjelenítés az Excel formátuma szerint · másoláskor pontos időpontok':'Display follows Excel formatting · copying preserves exact times',
    'Nincs másolható, lezárt munkanap.':'There are no completed days to copy.','Az időpont formátuma ÓÓ:PP legyen.':'Use the HH:mm time format.',
    'A művelet nem válaszolt. Próbáld újra.':'The operation did not respond. Please try again.',
    'Hiányos':'Incomplete','Folyamatban':'In progress','Lezárt':'Complete',
    'hétfő':'Monday','kedd':'Tuesday','szerda':'Wednesday','csütörtök':'Thursday','péntek':'Friday','szombat':'Saturday','vasárnap':'Sunday',
    'Hiányzik a nap kezdő feloldása.':'The first unlock of the day is missing.','Hiányzik a nap záró zárolása.':'The final lock of the day is missing.',
    'Több Windows-munkamenet: a munkaszakaszok kézi ellenőrzést igényelnek.':'Multiple Windows sessions: the work sections need manual review.',
    'Az eseménysorban hiányzó vagy ismétlődő esemény van.':'The sequence contains a missing or repeated event.',
    'A nap egy részében a naplózás ki volt kapcsolva.':'Logging was disabled for part of this day.',
    'A korábban választott ebédpár már nem található; válaszd ki újra.':'The previously selected lunch pair is no longer available. Select it again.',
    'Érvénytelen nap- vagy perchatár.':'Invalid history or minute limit.','Érvénytelen betűméret.':'Invalid font size.','Érvénytelen nyelv vagy téma.':'Invalid language or theme.',
    'A munkaidőfájl .xlsx formátumú legyen.':'The workbook must use the .xlsx format.','Teljes .xlsx fájlútvonal szükséges.':'An absolute .xlsx workbook path is required.',
    'Nincs Details munkalap a fájlban.':'The workbook has no Details worksheet.','Érvénytelen Details munkalap.':'Invalid Details worksheet.',
    'Hiányzik a Details fejléc.':'The Details header is missing.','A Details A–C fejlécének Date / From / To sorrendűnek kell lennie.':'Details columns A–C must have Date / From / To headers.',
    'Hiányoznak az Excel cellastílusai.':'The Excel cell styles are missing.','Érvénytelen megkezdett sor.':'Invalid unfinished row.',
    'Nincs mentendő sor.':'There are no rows to save.','Az Excel-fájl az előnézet óta megváltozott. Kérj új előnézetet.':'The workbook changed after the preview. Generate a new preview.',
    'Az előnézet lejárt. Kérj új előnézetet.':'The preview has expired. Generate a new preview.','A beállított .xlsx fájl nem található.':'The selected .xlsx workbook was not found.',
    'A Windows-jóváhagyás elmaradt. A kért beállítás módosítása nem történt meg.':'Windows approval was cancelled. The requested setting was not changed.',
    'A segédet ugyanazzal a Windows-fiókkal kell jóváhagyni.':'Approve the helper using the same Windows account.',
    'A Windows-segéd leállt.':'The Windows helper stopped.','Nem indult el a Windows-napló segédje.':'The Windows event-log helper could not start.'
    ,'Nem a várt Windows-segéd kapcsolódott.':'An unexpected process connected instead of the Windows helper.'
  };
  function text(value, language) {
    if(language!=='en')return value;
    const leading=value.match(/^\s*/)[0],trailing=value.match(/\s*$/)[0],s=value.trim();
    if(!s)return value;
    let translated=en[s];
    if(!translated){
      const rules=[
        [/^Kézi választás · (\d+) perc$/,'Manual selection · $1 min'],[/^Becsült · (\d+) perc$/,'Estimated · $1 min'],
        [/^(\d+) nap$/, '$1 days'],[/^(\d+) perc$/, '$1 min'],[/^(\d+) sor$/, '$1 rows'],[/^(\d+) munkanap$/, '$1 workdays'],
        [/^(\d+) új nap · (\d+) új sor(?: · (\d+) megkezdett sor)?$/,(_,d,r,p)=>`${d} new days · ${r} new rows${p?' · '+p+' unfinished rows':''}`],
        [/^(\d+) új sor · a lezárt, teljes napokból\. A Details leírás üres marad\.$/,'$1 new rows from completed days. The Details description stays blank.'],
        [/^Megkezdett sor befejezése \((\d+)\. sor\)$/,'Complete unfinished row (row $1)'],
        [/^Összes naplóesemény \((\d+)\)$/,'All log events ($1)'],
        [/^Legfeljebb 60 nap · a Windows-napló legrégebbi bejegyzése: (.+)$/,'Up to 60 days · oldest Windows log record: $1'],
        [/^Legfeljebb 60 nap · az elérhető napló alapján$/,'Up to 60 days · based on available log data'],
        [/^(.+) eseményei$/,'Events for $1'],[/^(.+) másolása$/,(_,label)=>`${text(label,'en')} — copy`],
        [/^Másolás: (.+)$/,'Copy: $1'],[/^Frissítve: (.+)$/,'Updated: $1'],[/^Saját Windows-fiók · (.+)$/,'Current Windows account · $1'],
        [/^Mentés kész\. Biztonsági másolat: (.+)$/,'Saved. Backup: $1'],
        [/^A napló frissítése nem sikerült: (.+)$/,(_,detail)=>'Could not refresh the event log: '+text(detail,'en')]
      ];
      for(const [pattern,replacement] of rules)if(pattern.test(s)){translated=s.replace(pattern,replacement);break;}
    }
    return leading+(translated||s)+trailing;
  }
  function apply(root,language) {
    document.documentElement.lang=language;
    const walker=document.createTreeWalker(root,NodeFilter.SHOW_TEXT);
    while(walker.nextNode()){
      const node=walker.currentNode;
      if(!['SCRIPT','STYLE','TEXTAREA'].includes(node.parentElement?.tagName))node.textContent=text(node.textContent,language);
    }
    root.querySelectorAll('[aria-label],[title],[placeholder]').forEach(node=>{
      for(const attr of ['aria-label','title','placeholder'])if(node.hasAttribute(attr))node.setAttribute(attr,text(node.getAttribute(attr),language));
    });
  }
  return {text,apply};
})();
