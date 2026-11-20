# Suito Ride – první verze

Samostatná aplikace pro Windows v C# (.NET 10 / WPF). Spusťte bin\Release\net10.0-windows10.0.19041.0\SuitoRide.exe. Spouštěcí soubor potřebuje ostatní soubory ve stejné složce a nainstalovaný .NET 10 Desktop Runtime.

1. Načtěte GPX s výškou v každém bodě a jedním souvislým úsekem. Ukázkový soubor je ukazka.gpx.
2. Pro náhled bez přístroje vyberte Ukázková jízda.
3. Suito zapněte, ukončete ostatní aplikace připojené k trenažéru, zapněte Bluetooth ve Windows a vyhledejte zařízení.
4. Vyberte Suito, připojte ho a zahajte jízdu. Zastavení je možné tlačítkem Zastavit.

Mapa zobrazuje GPX trasu a aktuální polohu jezdce. Lze zobrazit celou trasu nebo sledovat jezdce. Podklady OpenStreetMap a knihovna Leaflet vyžadují připojení k internetu; při zobrazení mapy se poskytovateli načítaných mapových dlaždic zpřístupní oblast mapy. Tlačítko Street View otevře aktuální polohu v externím prohlížeči; dostupnost panoramat závisí na Google a nejde o automaticky synchronizované video. Aplikace obsahuje katalog GPX souborů ve vybrané složce (bez podsložek); tlačítkem Obnovit katalog načtete změny. Poslední složka se ukládá v LocalAppData\SuitoRide. Mapa vyžaduje Microsoft Edge WebView2 Runtime. Skutečné jízdy se při dojezdu, ručním zastavení nebo přerušení ukládají do historie v LocalAppData\SuitoRide\rides. Ukázkové jízdy se neukládají. Čas je celkový čas jízdy; průměrný výkon se počítá z dostupných aktuálních údajů, nikoli z normalizovaného výkonu. Sklon se vyhlazuje na přibližně 50 metrech a omezuje na -10 až +15 %. Přesnost závisí na nadmořských výškách v GPX. Ujetá vzdálenost vychází z rychlosti trenažéru, nikoli z fyzikální simulace. Ne všechny trenažéry musí příkazy FTMS podporovat stejně. Komunikace s konkrétním přístrojem zatím nebyla ověřena.
## Panorama a orientace mapy

V této verzi se Mapy.com zobrazují přímo vedle mapy jako statické panoramatické snímky a za jízdy se obnovují po 20 metrech. Pohled míří podle směru GPX trasy. Pro zapnutí vyberte Mapy.com, vložte vlastní Mapy.com REST API klíč a stiskněte Zapnout panorama. Klíč se ukládá zašifrovaný pomocí ochrany Windows pro aktuální uživatelský účet. Pro další spuštění nechte pole prázdné; klíče Mapy.com a Googlu se ukládají odděleně. Klíč lze odstranit tlačítkem Odstranit uložený klíč. Snímky nejsou dostupné všude. Mapy.com počítají spotřebu v kreditech podle svého aktuálního ceníku.

Alternativně lze zvolit Google Street View a zadat Google Maps Embed API klíč s aktivovanou fakturací. Google zobrazuje interaktivní panorama. Pohledy přeskakují mezi snímky, nejedná se o plynulé video. Zaškrtnutím Mapa ve směru jízdy se mapa otočí podle GPX. Přepínač Sledovat jezdce ji udržuje na poloze jezdce.

Zdrojový projekt lze sestavit příkazem dotnet build -c Release. Během jízdy panorama vyplní většinu mapového panelu a mapa se zobrazí menší. Aplikace vyžaduje internet a Microsoft Edge WebView2 Runtime.
## Úprava trasy a start

Tlačítko Upravit trasu umožňuje přetažením značek na výškovém profilu nebo pomocí posuvníků vybrat začátek a cíl a obrátit směr vybraného úseku. Po potvrzení lze výsledek uložit do nového GPX souboru. Export obsahuje souřadnice a nadmořskou výšku trasy; ostatní údaje a rozšíření původního GPX nepřenáší. Bez uložení platí změna jen pro aktuální načtenou trasu.

Před skutečnou i ukázkovou jízdou běží pětisekundový odpočet, který lze přerušit tlačítkem Zastavit.

## Společná jízda

Oba jezdci potřebují tuto verzi aplikace a stejný soubor GPX (včetně směru a vybraného úseku). Každý ovládá svůj trenažér a zahajuje jízdu zvlášť; nejde o synchronizovaný start ani o sdílení panoramatu. Poloha a stav druhého jezdce se zobrazují na mapě.

Pro propojení přes internet bez změn routeru lze použít společnou virtuální soukromou síť, například Tailscale. Hostitel vytvoří v aplikaci společnou místnost; aplikace spustí přiložený server na portu TCP 5077. Kamarád zadá adresu hostitelova počítače ve VPN ve tvaru http://100.x.y.z:5077 a kód místnosti. Oba musí mít ve VPN povolené vzájemné spojení. Příchozí spojení může být nutné povolit ve firewallu Windows pro používané síťové rozhraní. Po dobu jízdy musí běžet aplikace hostitele i jeho počítač. Kód místnosti funguje jako přístupové heslo; sdílej ho pouze s účastníky.

Pro test použijte stejný GPX, připojte se do místnosti a na obou počítačích spusťte ukázkovou jízdu. Odpojení odstraní značku jezdce; při výpadku spojení je nutné se připojit znovu. Původní GPX ani data z trenažéru se nepřenášejí; přenáší se jméno, ujetá vzdálenost a stav jízdy. Server neukládá historii.

Server lze alternativně spustit samostatně z projektu SpolecnaJizda.Server. Server vystavený do veřejného internetu musí používat HTTPS, například prostřednictvím reverzní proxy. Tlačítko Vytvořit místnost spouští místní server hostitele; na samostatném serveru založíš místnost tlačítkem Vytvořit na serveru po zadání jeho adresy. Bezpečné nasazení, dostupnost portů a ověření sítě zajišťuje provozovatel.

Pro vlastní sestavení balíčku spusť ve složce projektu `dotnet build -c Release -o .\spolecna-jizda` a potom `dotnet publish SpolecnaJizda.Server\SpolecnaJizda.Server.csproj -c Release -r win-x64 --self-contained true -o .\spolecna-jizda\server`. Kamarád potřebuje celou složku se sestavením a nainstalovaný .NET 10 Desktop Runtime; server hostitele má přibalené vlastní běhové prostředí.