# ADR-0009: Ortam topolojisi ve holdout güven sınırı

- Durum: **Kısmen ADR-0013 ile değiştirildi** (2026-10-14, kullanıcı kararı D5 rev.: VPS yok) · İlk kabul: 2026-10-13 (teknik lider kararı T1–T3, T17, T19)
- Uygulama durumu: Madde 7'deki Claude Code korkulukları M0'da repoya eklendi (`CLAUDE.md`, `.claude/settings.json`; canlı denenen deny kuralları CHANGELOG'da). Geri kalanı **planlandı, uygulanmadı**.
- İlgili: ADR-0001, ADR-0006, ADR-0013, `architecture.md` §1 ve §6.2, `testing-strategy.md` §7

> **rev. 5 notu (2026-10-14):** Bu ADR'deki VPS'e dayanan kararlar (Karar madde 1, 2 ve 5'teki SSH/`docker save | ssh` kısmı; madde 6'daki H-ENV listesi; "Gerekçe"deki SSH ve H13 argümanı; StatusRelay container'ı) **ADR-0013 ile geçersizdir.** Holdout artık aynı yerel makinede, aynı PostgreSQL instance'ında ayrı bir veritabanındadır (kabul edilmiş artık risk). **Geçerli kalanlar:** madde 3'teki DB rolleri ve sonuç tablolarının ayrımı (StatusRelay container'ı hariç; `status_relay` rolünü artık evaluator tutar, D24), madde 4'teki evaluator API'si ve erken değerlendirmeye karşı iki katman, `HoldoutEnvironmentVerified`'ın yalnızca `Validated` yazımını kapılaması. Aşağıdaki metin tarihsel kayıt olarak korunmuştur.

## Problem ve kısıtlar
- Holdout verisi geliştirme kimlik bilgileriyle okunamamalı; tercihen geliştirme makinesinde hiç bulunmamalı; yalnızca ayrı bir evaluator rolüyle erişilmeli (D17).
- Claude Code geliştirme makinesinde komut çalıştırabiliyor ve geliştiricinin dosyalarına ve kimlik bilgilerine erişebiliyor.
- Veri kamuya açık; amaç gizlilik değil, kontaminasyonun önlenmesi.
- Önceki belgelerde şu çelişkiler vardı: "tek PostgreSQL" ile ayrı holdout DB'si; M3'te ayrı host ihtiyacı ile D5'te M5'te planlanan VPS; değerlendirme kanalının, ağ kurallarının ve deploy anahtarlarının tanımsız olması.
- Bütçe genişletilemez ve altyapı satın alınamaz (kullanıcı yetkisi).

## Alternatifler
| | Güvenlik | Maliyet | İşletim | Sonuç |
|---|---|---|---|---|
| A. M3'te ayrı bir holdout VPS'i | Güçlü | Ek sunucu, M3'ten itibaren | İki sunucu | Hayır. M3'te değerlendirilecek gerçek bir aday yok |
| **B. M5'teki D5 VPS'inde ayrı container, ayrı DB instance'ı ve ayrı ağlar; M3'te evaluator yerelde, sentetik veriyle** | Geliştirme makinesinden ağ ve kimlik düzeyinde ayrım. Trading ile aynı sunucuda ama ağlar ayrı | **Ek maliyet yok**; VPS zaten D5'te planlı | Tek sunucu | **Seçildi** |
| C. Geliştirme makinesinde ayrı OS kullanıcısı | Zayıf: Windows'ta yönetici ya da Docker erişimi sınırı aşabilir | Yok | Düşük | Hayır |
| D. CI'da geçici evaluator | Workflow geliştirme ortamından değiştirilebilir; uzak repo gerektirir | Belirsiz | Orta | Hayır |

## Karar
1. **Geliştirme makinesi:** kod, testler, holdout penceresi dışındaki veriyle dev DB'si, M4'te yerel Research.Host. Holdout verisi, holdout ya da VPS kimlik bilgisi ve VPS'e etkileşimsiz erişim **yok.**
2. **Prod VPS (M5):** dört Docker ağı. Hiçbir container portu host'a yayınlanmaz; dışa açık tek port SSH.

   | Ağ | Tür | Üyeler |
   |---|---|---|
   | `ops` | İnternet çıkışı var | Trading.Host, Research.Host, StatusRelay, ops DB |
   | `eval` | internal | Research.Host, StatusRelay, Evaluator |
   | `holdout` | internal | Evaluator, holdout DB (ayrı PostgreSQL instance'ı) |
   | `egress` | İnternet çıkışı var | Yalnızca Evaluator (kaynaktan import) |

3. **Kimlik bilgileri:**
   - Holdout DB parolası yalnızca evaluator secret'ında.
   - Research.Host'ta yalnızca **submit-only** bir evaluator token'ı var.
   - **(rev. 4.1)** Ayrı DB rolleri: `research_app`, `trading_app`, `market_import`, `ops_api`, `status_relay`, `eval_registry`, `eval_diagnostic`, `eval_prospective`.
     - Research yalnızca `research.*` tablo ve view'larını okur: kesimden önceki `market.candles` ve `cohort_status`'ta `revealed` olan kohortlar.
     - **`research.cohort_status`'a yalnızca `status_relay` yazabilir.** `status_relay` ayrı bir container'dır ve Research.Host bu kimliğe sahip değildir.
     - Canlı veri ayrı tabloda (`market.live_candles`) ve yalnızca Trading'in erişiminde.
     - Evaluator rollerinin ops DB'sine erişimi yok.
     - Sonuç tabloları ayrıdır: `results.diagnostic_results` (yalnızca `eval_diagnostic`; sonuç alanı yalnızca `NotRejected` ya da `Rejected`) ve `results.prospective_results` (yalnızca `eval_prospective`). `ValidationStatus` yalnızca ikincisinden türetilir.
     - `results.cohort_manifests`: `registered_at` ve `A_c` sunucu tarafından atanır; açık kohort sınırı, aile kotası ve K_D burada, `eval_registry` tarafından işlem içinde uygulanır.
     - İzin tablosunun tamamı `architecture.md` §6.2'de.
4. **Evaluator API'si (rev. 4):** `POST /cohorts` (manifesto), `POST /diagnostic-evaluations` ve `GET /cohorts/{id}/status`. Yanıtlarda yalnızca durum ve açığa çıkmış yuvarlanmış özet bulunur. Ham veri ya da zaman serisi döndüren bir uç nokta **yoktur.** "Şimdi değerlendir" diye bir uç nokta da yoktur; ileriye dönük değerlendirmeyi evaluator'ın zamanlayıcısı pencere kapandıktan sonra başlatır. Politika: ADR-0006.
   - **Erken değerlendirme ve yetkisiz import'a karşı iki katman:**
     1. enjekte edilebilir saatle uygulama kontrolü;
     2. penceresi kapanmamış kohort için **yalnızca** `prospective.*`, `results.prospective_runs` ve `results.prospective_results` tablolarına `INSERT`'i reddeden DB trigger'ı. Manifesto ve tanısal tablolar bu trigger'ın kapsamı dışındadır (manifesto, pencere açılmadan önce kaydedilmek zorundadır).

     İmport yalnızca kayıtlı kohort pencereleri için yapılır.
5. **Yönetim ve deploy (T19):**
   - VPS SSH anahtarı parolalı ve ssh-agent'a yüklenmez.
   - Deploy, `docker save | ssh vps docker load` ile insan tarafından **etkileşimli** yapılır; image digest'i `EvaluatorRelease` olarak kaydedilir.
   - Evaluator kodundaki her değişiklik deploy kontrol listesinde vurgulanır ve `HoldoutEnvironmentVerified`'ı sıfırlar.
   - Harici image registry kullanılmaz.
6. **Gerçek ortam doğrulaması:** `HoldoutEnvironmentVerified` yalnızca H-ENV testleri (H5, H6, H6b, H13, H14, H19, H20) gerçek ortamda geçtikten sonra, kullanıcı oturumuyla set edilir. Bu bayrak **yalnızca `Validated` sonucunun yazılmasını kapılar;** Testnet'i etkilemez (ADR-0012).
7. **Claude Code korkulukları (T17, M0):** proje talimat dosyası ve proje izin kurallarında deny kuralları. Bunlar ek bir savunma katmanıdır; güven sınırı, 2–6. maddelerdeki ağ ve kimlik kontrolleridir.

## Gerekçe
- Güven sınırı, kod konvansiyonuna değil; **ağ topolojisine, kimlik bilgisinin yokluğuna ve etkileşim gerektiren yönetim erişimine** dayanır. Claude Code etkileşimsiz çalıştığı için parolalı ve agent'a yüklenmemiş bir SSH anahtarını kullanamaz (H13 bunu doğrular).
- Dar API, Research'ün evaluator'a erişebilmesini ve holdout'u okuyamamasını aynı anda sağlar. Kalan risk (holdout'u tekrar tekrar sorgulamak) ADR-0006'nın politikasıyla sınırlanır.
- Seçenek B ek maliyet getirmez ve D5'in zamanlamasını korur.

## Kabul edilmiş riskler
- Veri kamuya açık; doğrudan indirilmesine karşı yalnızca süreç kuralı ve audit var.
- Kötü niyetli ya da hatalı bir evaluator sürümü, özet metriklere bilgi gizleyebilir. İnsan incelemesine ve etkileşimli deploy'a bağlı.
- Trading ile holdout aynı VPS'te. Bir host compromise'ı ikisini birden etkiler; tek kullanıcılı kişisel proje için kabul edildi.
- Geliştirici (insan) her zaman bu sınırı aşabilir. Kontrollerin amacı, araç ve iş akışı kaynaklı kazaları önlemektir.

## Kabul kriterleri
`testing-strategy.md` §7: M1'de H1–H4; M3'te sentetik evaluator testleri; M5'te H-ENV. `roadmap.md` ile uyumlu.
