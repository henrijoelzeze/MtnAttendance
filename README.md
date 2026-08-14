# MTN Attendance — noyau applicatif

## Ce qui est livré

| Fichier | Rôle |
|---|---|
| `Domain/Entities.cs` | Employee, HrAccount, Event, Attendance + normalisation du matricule |
| `Data/AppDbContext.cs` | Mapping, index uniques, détection des violations d'unicité |
| `Services/CheckInService.cs` | Les 4 vérifications du pointage |
| `Services/HrAuthService.cs` | Résolution du matricule en 5 issues, mots de passe, réinitialisation |
| `Services/EmployeeImportService.cs` | Import Excel : upsert, désactivation, recalcul RH, rapport |
| `Services/EventService.cs` | Création d'événement, QR, taux par département |
| `Components/Pages/Public/CheckIn.razor` | Écran employé (SSR statique) |
| `Components/Pages/Auth/Login.razor` | Connexion RH (SSR statique) |
| `Components/Pages/Admin/*.razor` | Events, EventForm, EventDashboard, Employees, HrAccounts |
| `Components/Layout/*.razor` | Bandeau back-office et en-tête mobile |
| `wwwroot/css/app.css` | Tokens et composants repris des frames Figma |
| `Program.cs` | DI, cookie d'authentification, endpoints QR / export / déconnexion |

## Mise en route

> Le projet n'a pas pu être compilé lors de sa génération (pas de SDK .NET ni d'accès NuGet
> dans l'environnement). Premier build à faire en local ; les versions des paquets ajoutés
> (EF Core 10.0.0, ClosedXML 0.104.2, QRCoder 1.6.0) sont à confirmer avec
> `dotnet list package --outdated` après le restore.

```bash
dotnet restore
dotnet ef migrations add Initial
dotnet ef database update
dotnet run
```

La chaîne de connexion est dans `appsettings.json` (SQL Server local par défaut).

## Écarts assumés par rapport aux maquettes

- Le bandeau affiche `(HR)` et non `(Super HR)` : la spec exclut tout niveau administrateur.
- « Required threshold for Q3 quorum » repose sur `Event.QuorumPercent`, optionnel : la ligne
  disparaît si aucun seuil n'est saisi.
- Les libellés d'interface sont en anglais, comme les frames. Le passage au français touche
  uniquement les fichiers `.razor` (aucune chaîne dans les services).
- Les tableaux sont écrits à la main plutôt qu'avec `RadzenDataGrid` : l'en-tête noir et les barres
  de progression coûtaient plus cher à surcharger qu'à écrire. Radzen reste en place pour la future
  liste nominative des présents (tri, pagination).

## Points d'attention

- **Rendu.** Aucun `@rendermode` global : le pointage et la connexion restent en SSR statique.
  Les pages du back-office ajoutent `@rendermode InteractiveServer` individuellement. Si un rendu
  interactif global est activé plus tard, il faut poser `[ExcludeFromInteractiveRouting]` sur ces
  deux pages, sinon la connexion casse (`SignInAsync` ne peut plus écrire le cookie).
- **Ordre des CSS** dans `App.razor` : Fluent d'abord, Radzen ensuite.
  ```html
  <link rel="stylesheet" href="_content/Microsoft.FluentUI.AspNetCore.Components/css/reboot.css" />
  <link rel="stylesheet" href="_content/Radzen.Blazor/css/material-base.css" />
  ```
- **Matricule.** `PersonNumberKey` est la clé de recherche (majuscules, zéros de tête retirés) :
  un fichier Excel qui transforme `0468089` en `468089` reste compatible avec les pointages passés.
- **Premier import.** Aucun compte RH n'existe avant le premier import ; ils sont créés
  automatiquement pour toute ligne dont `Subdivision = "Human Resources"`.
- **Déconnexion en GET.** Le bandeau est rendu en interactif, où `<AntiforgeryToken />` n'a pas de
  `HttpContext`. Pour repasser en POST, sortir le bouton dans un composant SSR statique.
- **Fuseau.** `TimeProvider.GetLocalNow()` est utilisé partout : fixer `TZ=Africa/Abidjan` sur le
  serveur, ou passer à un `TimeProvider` explicite si l'hébergement est en UTC.
