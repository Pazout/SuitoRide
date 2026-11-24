# Suito Ride – příručka pro agenty

Suito Ride je desktopová aplikace pro Windows v C# a WPF (.NET 10) pro jízdu po GPX na trenažéru s Bluetooth LE FTMS, například Elite Suito. Aplikace načítá katalog GPX z místní složky, umožňuje vybrat úsek a směr jízdy, mění odpor podle sklonu a zaznamenává telemetrii. Windows GATT zajišťuje připojení zařízení a WebView2 zobrazuje mapy. Ukázková jízda funguje bez zařízení. Skutečné jízdy se ukládají do historie a lze je exportovat do TCX nebo odeslat do připojených služeb. Tato příručka popisuje práci v repozitáři. Uživatelský návod je v [README.md](README.md). Všechny zde uvedené cesty ke zdrojům jsou relativní ke kořenu repozitáře.

## Začátek práce a sestavení

- Spusť `git status --short` a prohlédni aktuální diff. V pracovním stromu mohou být rozpracované změny z předchozích úkolů. Zachovej je.
- Verzi aplikace určuje vlastnost `Version` v [SuitoRide.csproj](SuitoRide.csproj). Zobrazuje se v titulku. Poslední lokální commit zjistíš přes `git log -1 --oneline`, vydanou verzi přes GitHub Releases.
- Pro rychlou kontrolu kompilace použij na Windows s .NET 10 SDK `dotnet build .\SuitoRide.csproj -c Release`. `ukazka.gpx` stačí pro ukázkovou jízdu bez zařízení. Mapové pohledy vyžadují WebView2 Runtime a připojení k internetu.
- `& .\Sestavit.ps1` sestaví desktop do stálé složky `app` a server do `app\server`. Skript sestavení odmítne, pokud z této složky běží Suito Ride. Při otevřené aplikaci lze ověřit kompilaci běžným příkazem výše a aktualizovat `app` po zavření. Pro běžná lokální sestavení používej stálou složku `app`.
- Repozitář nemá vlastní testovací projekt. V závěru úkolu uveď, co bylo ověřeno sestavením, ukázkovou jízdou a případně se skutečným zařízením. Ukázka umí ověřit mapy a ovládání, fyzickou změnu odporu nebo příjem tepu ověříš připojeným zařízením.

## Kde hledat

| Oblast | Soubory |
| --- | --- |
| Hlavní okno, GPX, katalog, obnova trasy, měření jízdy a FTMS | [MainWindow.xaml](MainWindow.xaml), [MainWindow.xaml.cs](MainWindow.xaml.cs) |
| Tlačítka, ikony a společný styl oken | [ActionButton.cs](ActionButton.cs), [App.xaml](App.xaml), [SuitoDialog.cs](SuitoDialog.cs) |
| GPX profil, výběr startu a cíle, otočení směru | [ElevationProfile.cs](ElevationProfile.cs), [MainWindow.ProfileTelemetry.cs](MainWindow.ProfileTelemetry.cs), [RouteEditWindow.cs](RouteEditWindow.cs) |
| BLE vyhledávání, identita a výběr zařízení, měření tepu | [BluetoothDeviceConnection.cs](BluetoothDeviceConnection.cs), [BluetoothDeviceWindow.cs](BluetoothDeviceWindow.cs), [MainWindow.HeartRate.cs](MainWindow.HeartRate.cs) |
| Základní mapa Leaflet, navigace MapLibre, přehled a panoramata | [map/index.html](map/index.html) |
| Kamera Google 3D | [map/google3d/view.js](map/google3d/view.js) |
| Nastavení map a klíčů | [MapSettingsWindow.cs](MapSettingsWindow.cs), [MapApiKeySettings.cs](MapApiKeySettings.cs), [PanoramaKeyStore.cs](PanoramaKeyStore.cs) |
| Historie, TCX a odesílání jízd | [RideHistory.cs](RideHistory.cs), [HistoryWindow.cs](HistoryWindow.cs), [TcxExporter.cs](TcxExporter.cs), [StravaService.cs](StravaService.cs), [FitnessSyncerService.cs](FitnessSyncerService.cs), [IntervalsIcuService.cs](IntervalsIcuService.cs), [DropboxRideArchive.cs](DropboxRideArchive.cs) |
| Odpočet, společná jízda a aktualizace | [CountdownWindow.cs](CountdownWindow.cs), [SharedRideWindow.cs](SharedRideWindow.cs), [server SignalR](SpolecnaJizda.Server/Program.cs), [AppUpdates.cs](AppUpdates.cs) |
| Překlady | [UiText.cs](UiText.cs), [Localization/](Localization/), [map/localization.js](map/localization.js) |

## Chování, které zachovat při změnách

- **Rozvržení.** Hlavní okno startuje maximalizované ve využitelné ploše Windows. Při jízdě přejde přes hlavní panel na stejném monitoru a po jízdě se vrátí do předchozího stavu. Mapa a výškový profil se dělí o dostupnou výšku, spodní telemetrie a ovládání mají vlastní řádky. Při změně panelů ověř malé okno i stav, kdy se současně připojují obě zařízení.
- **Tlačítka.** Pro vlastní tlačítka WPF používej ActionButton a odpovídající ikonu z ButtonIcons. Nech popisek textový pro čtečky obrazovky a zachovej společný kontrastní styl. Pokud akce otevírá dialog nebo vyžaduje další vstup, použij v XAML parametr Prompt=True nebo v C# prompt: true. Tři tečky doplňuje UiText až za přeložený text. Přímo vykonané akce jako Zastavit a Obnovit katalog mají samotný popisek.

- **Bluetooth.** Po načtení hlavního okna se nezávisle hledá poslední úspěšně připojený trenažér a hrudní pás. Pět minut je horní limit automatického hledání od spuštění aplikace. Ruční vyhledání nebo připojení ukončí automatický pokus jen u příslušného typu zařízení a jen v daném spuštění. Úspěšné připojení aktualizuje uloženou identitu. Zachovej čtení starších záznamů trenažéru obsahujících jen adresu a jméno.
- **SRAM AXS.** Dialog hledá podle názvu v BLE reklamě. Podporovaná přehazovačka poskytuje standardní Battery Service. Čtení probíhá při výběru zařízení a poté jednou za minutu. Stav zůstává viditelný při jízdě. Vybrané zařízení se ukládá do last-sram.json. U testované Force AXS posílá změny GATT d9050009 a čtení stejné charakteristiky vrátí polohu zadního měniče v poli 21. Tento kanál čteme přednostně. Starší šifrované čtení d9050021 používá uložený klíč a hodnotu ukáže jen po zachycení změny. Párovací klíč se ukládá samostatně pro každé zařízení s ochranou Windows DPAPI. Párování v Suito Ride nebo v mobilní aplikaci může změnit platný klíč. Testuj s fiktivními klíči a reálné klíče nikdy nevypisuj. Po neúspěšném čtení se ukáže pomlčka. Hledání, volba a čtení fungují nezávisle na trenažéru i pásu.
- **Indikátory BLE.** Pod údajem Tep je stav pásu, pod údajem Výkon stav trenažéru. Oba mohou být vidět současně. Text hledání se po nalezení změní na připojování a po skončení pokusu zmizí. Tlačítka pro otevření dialogů jsou při jízdě skrytá a telemetrie zůstává viditelná.
- **Mapy.** Poloha jezdce vychází z ujeté vzdálenosti po právě načteném úseku GPX. MapLibre plynule sleduje jezdce a směr nezávisle na přepínačích hlavní mapy. Jeho zoom se pamatuje. Při běžící navigaci se přepínače hlavní mapy startem jízdy automaticky nezapínají. Při změně výpočtu směru ověř ostrou zatáčku, obrácený GPX, přechod přes sever a konec trasy.
- **Profil.** Kliknutím na malý profil se přepíná dosah 50, 100, 150, 250, 500 m a zpět na 50 m. Velký profil zobrazuje rychlost, tep, kadenci a výkon na samostatných stupnicích v legendě. Pokud údaj chybí nebo je zastaralý, křivka má mezeru.
- **Jízda a export.** Ukázka má odpočet 3–2–1 a skutečná jízda 10 sekund. Krátké tiky zazní na 3, 2 a 1. Start má delší tón. Zvuky jsou vložené jako WAV ve složce Assets a okno odpočtu patří na monitor aplikace. Ukázka se do historie nezapisuje. Tep je volitelný ve vzorcích a TCX, aby šly číst i starší záznamy. Po pěti sekundách bez nové hodnoty aplikace starý tep dál nepoužívá. Nové záznamy obsahují polohu a výšku z GPX. Stejný TCX používá ruční export, místní archiv Dropboxu a odesílání na Stravu, FitnessSyncer a Intervals.icu. Dropbox pracuje s místní složkou synchronizovanou klientem Windows.
- **Překlady.** Výchozí jazyk vychází z regionálního formátu Windows, ruční nastavení se použije při příštím spuštění. České texty tvoří klíče. Při změně přeložitelného popisku aktualizuj odpovídající překlady pro angličtinu, němčinu, francouzštinu, španělštinu, japonštinu, polštinu a slovenštinu. Texty v mapové vrstvě mají vlastní lokalizaci.

## Uživatelská data a vydání

- Nastavení, WebView2 profil a historie jsou v `%LOCALAPPDATA%\SuitoRide`. Poslední zařízení se ukládají do `last-trainer.json` a `last-heart-rate.json`, historie do `rides`. Citlivé klíče a tokeny jsou uložené místně se zabezpečením aktuálního účtu Windows. Pro příklady a testy používej smyšlené hodnoty.
- [Pazout/SuitoRide](https://github.com/Pazout/SuitoRide) je veřejný repozitář. Uživatelské klíče, hesla ani tokeny nesmějí být ve zdrojích, v žádném publikovaném commitu (včetně historie) ani v ZIPu. Osobní OAuth klientská ID se zadávají v aplikaci. Před publikováním zkontroluj zdroje, historii a obsah výsledného ZIPu. Do balíčku ber pouze čisté výstupy sestavení, mimo uživatelský profil.
- Pro kontrolu aktualizací musí být release veřejný a stabilní. Tag má formát `vX.Y.Z` a příloha `SuitoRide-vX.Y.Z-win-x64.zip`. Pro distribuční ZIP použij samostatně spustitelný publish pro Windows x64 (`-r win-x64 --self-contained true`). V kořeni ZIPu musí být `SuitoRide.exe` se souhlasící verzí a `map/index.html`. Aktualizátor ověřuje velikost a SHA-256 přílohy podle metadat GitHubu. Osobní aktualizace instaluje pod `%LOCALAPPDATA%\SuitoRide\installed`.
- Společnou instalaci rozpozná MachineUpdates podle cesty Program Files\SuitoRide\SuitoRide.exe. Zástupce ve Startu míří na stejnou cestu i po aktualizaci. Zvýšená oprávnění používá jen pomocný proces, který samostatně ověří veřejné vydání a ZIP, počká na ukončení aplikace a vymění složku. Při neúspěšné výměně vrátí původní složku. Spouštění aplikace i ukládání uživatelských dat probíhá pod účtem uživatele.
- Commit, push a vydání prováděj, pokud jsou součástí aktuálního zadání. Po vydání ověř na GitHubu stabilní release, název nahrané přílohy a dostupnost jejího SHA-256.
