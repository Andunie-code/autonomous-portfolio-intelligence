# Autonomous Portfolio Intelligence — Claude Code talimatları

Kişisel bir kripto spot portföy araştırma ve paper/Testnet trading sistemi (.NET 10, modüler monolit).
Kullanıcıyla Türkçe iletişim kurulur; teknik terimler İngilizce kalabilir.

## Kaynaklar
- Gereksinimler ve karar etiketleri (U/T/P/TBD): `docs/requirements.md`
- Mimari: `docs/architecture.md` · ADR'ler: `docs/adr/` · Yol haritası: `docs/roadmap.md`
- Belge değişiklikleri `docs/CHANGELOG.md`'ye yazılır.

## Çalışma kuralları
- Her milestone ayrı kullanıcı onayıyla başlar. Onaylanmamış bir milestone'a kendiliğinden geçilmez.
- Teknik kararlar alınabilir; gerekçe ilgili ADR'ye ve CHANGELOG'a yazılır.
- "Uygulandı" ya da "geçti" ifadesi yalnızca gerçekten çalıştırılmış ve geçmiş testler için kullanılır.
- Doğrulama: `powershell -ExecutionPolicy Bypass -File scripts/verify.ps1`. Yerel ortam: `powershell -ExecutionPolicy Bypass -File scripts/dev-up.ps1`. (Windows ExecutionPolicy script çalıştırmayı engelliyorsa `-ExecutionPolicy Bypass` gerekir; alt script çağrıları da bu bayrakla yapılır.)

## Onay gerektiren işlemler
Kullanıcıdan açık onay alınmadan yapılmaz:
- Runtime AI API çağrısı (Gemini dahil) ya da herhangi bir Anthropic runtime API kullanımı.
- Ücretli servis kullanımı, altyapı oluşturma veya satın alma, D4 bütçesini aşan harcama. (VPS kullanılmaz; sistem yerel tek makinede çalışır, ADR-0013.)
- Gerçek ya da simüle edilmiş alım satım, borsa hesabına bağlanma, risk limitlerini gevşetme.
- Uzak depoya **her push** (her seferinde ayrı onay) ve herhangi bir yayın, paylaşım ya da erişim yetkisi değişikliği. Force-push yapılmaz.

## Holdout kuralları (ADR-0006, ADR-0009, ADR-0013)
- Araştırma kesim tarihi `2026-01-01T00:00:00Z`. Bu tarih ve sonrasına ait piyasa verisi Claude Code tarafından **indirilmez, okunmaz ve incelenmez**; WebFetch, curl, script ya da başka yolla da (tanısal ve ileriye dönük holdout, ADR-0006). Bu veriyi yalnızca HoldoutEvaluator (holdout) ve Trading.Host (canlı veri) kendi Docker volume'lerine alır.
- Holdout aynı yerel makinede, aynı PostgreSQL instance'ında ayrı bir `holdout` veritabanındadır (kabul edilmiş artık risk, ADR-0013). Holdout veritabanına ve HoldoutEvaluator container'ına bağlanılmaz; `docker exec`, `docker cp`, volume bağlama ya da superuser ile holdout verisi okunmaz. Holdout verisi repo dizinine yazılmaz.
- Kohort manifestoları (`docs/cohorts/` ve yetkili kayıt) ile değerlendirme sonuçları değiştirilmez ve silinmez; yalnızca yeni kayıt eklenir. `Validated` durumu yalnızca ileriye dönük evaluator'dan gelir.
- Açığa çıkmamış kohortların paper ve Testnet sonuçları ile tanısal metrikler, kullanıcı paylaşsa bile **araştırma ya da hipotez girdisi olarak kullanılmaz.**
- Holdout veritabanına ve evaluator'a erişim denenmez. SSH bağlantısı kurulmaz.
- Holdout koruma kodu (adında `Holdout` geçen dosyalar) ve evaluator kodu, kullanıcının açık isteği olmadan değiştirilmez.
- **M5 yerel ortamı (m5-protocol §4, §11):** `ops` veritabanına yalnızca `research_app` login'iyle bağlanılır. `market.live_candles`, `trading.*` ve canlı veriden türeyen tablolar (korelasyon, PnL) okunmaz. `scripts/m5-*.ps1` (bootstrap, deploy, henv, paper-start, paper-report, backup, restore-test), evaluator ve probe komutları ile Trading.Host'un başlatılması **yalnızca kullanıcı** tarafından çalıştırılır. Paper raporlarından yalnızca operasyonel alanlar (m5-protocol §7.3) görülür.
- Holdout ortamına ait sır adları (`tests/Architecture.Tests/HoldoutSecretReferenceTests.cs`) evaluator projesi dışında hiçbir kaynak, config ya da script dosyasında kullanılmaz.

## Sırlar
- `.env`, API anahtarları, token'lar, parolalar ve özel anahtarlar repoya eklenmez, okunmaz, loglanmaz ve prompt'lara konmaz.
- Loglama yalnızca `AddPortfolioLogging` ile yapılır (redaction).
- Commit ve push öncesinde `scripts/scan-secrets.ps1` çalıştırılır.
