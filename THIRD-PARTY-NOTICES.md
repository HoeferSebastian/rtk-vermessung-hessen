# Drittkomponenten und Datenquellen

## Im Repository enthalten

| Komponente | Verwendung | Lizenz |
|---|---|---|
| [Leaflet](https://leafletjs.com/) 1.9.4 (`src/Ntrip.Wpf/Map/leaflet/`) | Kartenanzeige | BSD-2-Clause, © Vladimir Agafonkin, Leaflet-Mitwirkende |

## Über NuGet eingebunden

| Paket | Verwendung | Lizenz |
|---|---|---|
| [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2) | Karte in der WPF-Oberfläche | Microsoft-Lizenz (siehe Paket) |
| [Microsoft.Data.Sqlite](https://www.nuget.org/packages/Microsoft.Data.Sqlite) | Speicher für Offline-Kacheln | MIT |
| [System.IO.Ports](https://www.nuget.org/packages/System.IO.Ports) | Serielle Schnittstelle zum GNSS-Modul | MIT |

## Datenquellen (werden zur Laufzeit abgerufen, nicht mitgeliefert)

| Quelle | Inhalt | Bedingungen |
|---|---|---|
| Hessische Verwaltung für Bodenmanagement und Geoinformation (HVBG) – WMS | ALKIS-Liegenschaftskarte, Luftbild DOP20 | Open Data: Nutzung laut Dienstbeschreibung ohne Einschränkung oder Bedingung erlaubt, automatisierter Abruf kostenfrei (§ 18 und § 24 HVGG). Quellenhinweis: „Geobasisdaten © HVBG Hessen“ |
| SAPOS® Hessen (NTRIP-Caster) | RTK-Korrekturdaten | Zugang mit eigenen Zugangsdaten; Nutzungsbedingungen der HVBG |
| OpenStreetMap (nur optional, nur online) | Straßenkarte | © OpenStreetMap-Mitwirkende, ODbL; [Tile Usage Policy](https://operations.osmfoundation.org/policies/tiles/) beachten |
| Esri World Imagery (nur optional, nur online) | Luftbild | Nutzungsbedingungen von Esri |
| OpenTopoMap (nur optional, nur online) | Topographische Karte | © OpenTopoMap (CC-BY-SA), OSM-Mitwirkende |
