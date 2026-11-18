# Cloud Storage: evidence

| Scenario | Prediction | Command, result and exit status | What changed in the catalogue and bucket? |
| --- | --- | --- | --- |
| Publish the original | Den oprindelige rapport forventes uploadet, verificeret og registreret i kataloget. | `dotnet run --project src/MobilityTicketing.Reports -- publish R-001 fixtures/validations-v1.jsonl`<br>Resultat: `R-001`<br>Exit status: 0 | R-001 blev registreret i kataloget. En ny version blev uploadet til S3-bucket med VersionId `ca59d158-5dc8-487c-b741-cc42d1e807b0`. |
| Interrupt after staging a correction | Den nye rapportversion forventes uploadet, og en receipt oprettes, uden at rapporten registreres i kataloget endnu. | `dotnet run --project src/MobilityTicketing.Reports -- stage R-002 fixtures/validations-v2.jsonl`<br>Resultat: `3af638da-3c3e-424b-9494-e8fde9e282c8`<br>Exit status: 0 | En ny version blev oprettet i S3-bucket for R-002. Receipt blev gemt lokalt, men R-002 var endnu ikke registreret i kataloget. Den tidligere version R-001 blev bevaret. |
| Recover with a mismatched receipt | Recovery forventes afvist, fordi checksum i receipt ikke stemmer overens med det gemte objekt. | `dotnet run --project src/MobilityTicketing.Reports -- recover R-002`<br>Resultat: `Report verification failed: Mismatch`<br>Exit status: nonzero (ikke registreret) | Receipt blev midlertidigt ændret med en forkert checksum. Kataloget blev ikke opdateret, og objektet i S3 forblev uændret. Den oprindelige receipt blev efterfølgende gendannet. |
| Recover with a nonexistent predecessor | Recovery forventes afvist, fordi den angivne forgænger ikke findes i kataloget. | `dotnet run --project src/MobilityTicketing.Reports -- recover R-002 NONEXISTENT`<br>Resultat: PostgreSQL foreign key-fejl `23503`.<br>Exit status: nonzero (ikke registreret) | Objektet blev verificeret, men PostgreSQL afviste registreringen, fordi forgængeren ikke fandtes. Der blev ikke oprettet en ny katalogpost, og S3-objektet forblev uændret. |
| Recover and repeat recovery | Recovery forventes at registrere R-002 med R-001 som forgænger. En gentagelse med samme metadata forventes at kunne gennemføres uden konflikt. | `report recover R-002 R-001`<br>Resultat første gang: `R-002`<br>`report recover R-002 R-001`<br>Resultat anden gang: `R-002`<br>Begge gennemførelser lykkedes. Exit statuses blev ikke særskilt registreret. | R-002 blev registreret i kataloget med R-001 som forgænger. Den gentagne recovery lykkedes uden at oprette en ny S3-version. En tidligere test havde registreret R-002 uden forgænger; den katalogpost blev slettet, hvorefter recovery blev gentaget med R-001 som forgænger. Receipt og S3-version blev bevaret. |
| Recover an already registered report | En recovery uden den oprindelige forgænger forventes afvist, når rapporten allerede er registreret med en forgænger. | `report recover R-002; echo "Exit status: $?"`<br>Resultat: `ReportConflict`<br>Exit status: 1 | Kataloget forblev uændret. R-002 beholdt R-001 som forgænger, og der blev ikke uploadet en ny S3-version. |
| Verify the original after the correction | R-001 forventes stadig at kunne verificeres mod sin oprindelige S3-version, selvom R-002 er nyere. | `report verify R-001`<br>Resultat: `Matches` | Ingen ændringer. Verificeringen brugte det oprindelige VersionId, der er gemt for R-001, frem for blot at læse den nyeste objektversion. |
| Verify with object storage stopped | Verificeringen forventes at fejle, fordi objektet ikke kan læses, når object storage er stoppet. | `lab_compose stop object-store`<br>`report verify R-001`<br>Resultat: `ReadFailed`<br>Exit status: ikke registreret | Kataloget forblev tilgængeligt og uændret. S3-objektet blev ikke ændret, men kunne ikke læses, mens object storage var stoppet. |
| Verify with PostgreSQL stopped | Verificeringen forventes at fejle, fordi rapportens metadata ikke kan læses fra kataloget. | `lab_compose stop postgres`<br>`report verify R-001; echo "Exit status: $?"`<br>Resultat: `CatalogueReadFailed`<br>Exit status: 4 | Objektlageret var stadig tilgængeligt, men katalogopslaget fejlede, fordi PostgreSQL var stoppet. Der blev ikke ændret noget i kataloget eller S3-bucket. |
| Verify after restarting PostgreSQL | Verificeringen forventes at fungere igen, når PostgreSQL er startet og klar. | `lab_compose start postgres`<br>`lab_compose ps`<br>PostgreSQL viste status `Up 6 seconds (healthy)`.<br>`report verify R-001; echo "Exit status: $?"`<br>Resultat: `Matches`<br>Exit status: 0 | Ingen ændringer i kataloget eller S3-bucket. Verificeringen fungerede igen, da PostgreSQL var tilgængelig. |

## Version IDs and row counts

| Report | S3 Version ID | Row count |
| --- | --- | --- |
| R-001 | `ca59d158-5dc8-487c-b741-cc42d1e807b0` | 3 |
| R-002 | `3af638da-3c3e-424b-9494-e8fde9e282c8` | 4 |

Begge rapporter refererer til forskellige versioner af det samme objekt:

`validation-exports/2026-11-20/validations.jsonl`

Versionering er aktiveret på S3-bucket, så uploaden af R-002 overskrev eller fjernede ikke den oprindelige version fra R-001.

Den seneste kontrol med `s3 s3api list-object-versions` viste præcis de to versioner. R-002 var den nyeste version (`IsLatest: true`), mens R-001 stadig var bevaret (`IsLatest: false`). Der blev ikke oprettet ekstra versioner under recovery.

## An additional check

Efter genstart af object storage med:

`lab_compose start object-store`

viste containeren status `Up`. Verificering af R-001 returnerede derefter `Matches`.

R-002 blev også verificeret og returnerede `Matches`.

Det bekræftede, at verificeringen fungerede igen, efter at object storage blev tilgængeligt.

## Storage decision

Løsningen bruger PostgreSQL til rapportkataloget og S3-kompatibel objektlagring til rapportfilerne.

Kataloget gemmer rapporternes metadata og det præcise S3 VersionId. Det gør det muligt at verificere en ældre rapportversion, selvom en nyere version er blevet uploadet.

S3-versionering bevarer tidligere objektversioner, mens receipt indeholder checksum og antal rækker, som bruges til at verificere en rapport under recovery.

Ved at holde katalog og objektlagring adskilt kan katalogets metadata stadig tilgås, hvis objektlageret midlertidigt bliver utilgængeligt. Omvendt kan objektlageret stadig være tilgængeligt, selvom PostgreSQL er stoppet. Verificering kræver dog adgang til begge dele og fejler derfor forskelligt afhængigt af, hvilken service der er utilgængelig.

## Ownership

Løsningen adskiller data efter deres ansvar.

De oprindelige valideringsrækker tilhører den oprindelige datakilde. Den færdige eksportfil gemmes som bytes i S3-bucket. PostgreSQL indeholder rapportkataloget med metadata som rapport-ID, antal rækker, checksum, forgænger og det præcise S3 VersionId.

En receipt gemmes lokalt som information om en upload, der endnu ikke er registreret i kataloget. Den indeholder blandt andet bucket, object key, VersionId, checksum og antal rækker. Det gør det muligt at genoptage registreringen efter en afbrudt proces.

Når systemet skal finde R-001, slår det rapport-ID'et op i PostgreSQL og bruger det gemte VersionId til at hente den korrekte objektversion fra S3. Det betyder, at R-001 stadig kan findes med sine oprindelige tre rækker, selvom R-002 senere har uploadet en nyere version med fire rækker.

## Safe retries

Hvis en S3-upload fejler, skal rapporten ikke registreres i PostgreSQL. Der skal heller ikke oprettes en receipt med et ugyldigt eller manglende VersionId.

Hvis uploaden lykkes, men katalogskrivningen fejler, kan recovery bruge den lokale receipt. Den indeholder det præcise S3 VersionId, checksum og antal rækker. På den måde kan systemet verificere den eksisterende version og forsøge at registrere rapporten igen uden at uploade filen på ny.

Hvis programmet crasher efter uploaden, men før receipt bliver gemt, kan objektet stadig eksistere i S3 uden en tilhørende receipt eller katalogpost. Det betyder, at systemet ikke automatisk har de nødvendige oplysninger til at genoptage processen.

Den samme recovery kan gennemføres flere gange, fordi systemet genbruger det gemte VersionId og kontrollerer rapportens ID og metadata i kataloget. Det kaldes idempotency. Gentagne recovery-forsøg opretter derfor ikke nye S3-versioner, når rapporten allerede er registreret med de samme metadata.

## Access and retention

Systemet skal kontrollere brugerens rettigheder, før det slår rapporten op i kataloget. Det forhindrer, at brugere uden adgang kan få oplysninger om rapporter.

Storage-identiteten skal kun have de nødvendige rettigheder. Den skal kunne uploade rapportfiler, læse eksisterende filer og udføre sletning, hvis en oprydningsproces har brug for det.

En oprydningsproces skal kunne skelne mellem afventende uploads og publicerede rapporter. En afventende upload har en lokal receipt, men er endnu ikke registreret i PostgreSQL. En publiceret rapport har en katalogpost, som refererer til et bestemt S3 VersionId.

Oprydningsprocessen kan derfor identificere uafsluttede uploads og fjerne dem efter en passende periode. Den må ikke slette S3-versioner, som stadig refereres af rapporter i kataloget.