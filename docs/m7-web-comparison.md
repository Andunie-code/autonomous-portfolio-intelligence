> Betimleyici rapor (m7-protocol §9). İstatistiksel iddia taşımaz ve hiçbir kapıyı, eşiği ya da kararı etkilemez.

# Web'li ve web'siz araştırma karşılaştırması

- Protokol: `m7-protocol v1.2` · tarih 2026-10-28
- **Web'li kol boş:** web research is closed: FindingExtraction has no qualified model (D20). FindingExtraction yeterliliği `Failed` sonuçlandı (`TaskQualification` 2; [`m7-fe-qualification-results.md`](m7-fe-qualification-results.md)). Production'da bulgu çıkarılmadı, bu yüzden bulgudan hipotez yazılamadı.
- **Web'siz kol koşulmadı:** karşılaştırılacak bir web'li kol olmadığı için, araştırma penceresinde gerçek denemeleri global sayaca (T18) ekleyecek bir web'siz koşu yapılmadı. Global deneme sayacı bu rapor için değişmedi.
- **Rapor nasıl üretildi:** `web-compare` aracı koşturulmadı; rapor onun kapalılık biçimiyle elle yazıldı (`WebComparison.Markdown`, m7-protocol §9 ve §14.5).

| Kol | Hipotez | Deneme (nokta) | Kapıları geçen hipotez | Hata | Extractor maliyeti | En sık takılan kapılar |
|---|---|---|---|---|---|---|
| web'li (bulgulardan) | 0 | 0 | — | — | 0.000000 USD | — |
| web'siz | — | — | — | — | — | koşulmadı |

## Kriterin karşılanma biçimi (roadmap M7 kriteri 7)
- m7-protocol §9: nitelik alınmazsa kriter, aracın hazır olması ve sentetik bulgularla çevrimdışı testinin geçmesiyle karşılanır.
- **Araç:** `BacktestCli web-compare` ve `scripts/m7-web-compare.ps1` (`e82e4dd`).
- **Çevrimdışı testler** (`verify.ps1` içinde):
  - `M7ComparisonReportTests` (kapalı ve açık durumlarda rapor biçimi);
  - `M7FindingHypothesisTests` (sentetik, doğrulanmış bir bulgudan yazılan hipotez normal hattan geçer: DSL, deneme sayacı, G1–G6; doğrulanmamış ya da bilinmeyen bulgu hipotezi deneme açılmadan reddettirir).
- **Yeniden değerlendirme koşulu:** FindingExtraction ileride yeni bir döngüyle (yeni prompt, protokol ve set) `Passed` alırsa ve kullanıcı `qualifiedModels` girişini eklerse, bu rapor `web-compare` ile gerçek iki kolla yeniden üretilir.
