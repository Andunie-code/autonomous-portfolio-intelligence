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
- Doğrulama: `powershell -File scripts/verify.ps1`. Yerel ortam: `powershell -File scripts/dev-up.ps1`.

## Onay gerektiren işlemler
Kullanıcıdan açık onay alınmadan yapılmaz:
- Runtime AI API çağrısı (Gemini dahil) ya da herhangi bir Anthropic runtime API kullanımı.
- Ücretli servis kullanımı, altyapı (VPS vb.) oluşturma veya satın alma, D4 bütçesini aşan harcama.
- Gerçek ya da simüle edilmiş alım satım, borsa hesabına bağlanma, risk limitlerini gevşetme.
- Uzak depoya push dışında herhangi bir yayın, paylaşım ya da erişim yetkisi değişikliği.

## Holdout kuralları (ADR-0006, ADR-0009)
- Holdout penceresine ait piyasa verisi bu makineye **indirilmez, okunmaz ve incelenmez**; WebFetch, curl, script ya da başka yolla da.
- Holdout veritabanına, evaluator'a ya da VPS'e erişim denenmez. VPS'e SSH bağlantısı kurulmaz.
- Holdout koruma kodu (adında `Holdout` geçen dosyalar) ve evaluator kodu, kullanıcının açık isteği olmadan değiştirilmez.
- Holdout ortamına ait sır adları (`tests/Architecture.Tests/HoldoutSecretReferenceTests.cs`) evaluator projesi dışında hiçbir kaynak, config ya da script dosyasında kullanılmaz.

## Sırlar
- `.env`, API anahtarları, token'lar, parolalar ve özel anahtarlar repoya eklenmez, okunmaz, loglanmaz ve prompt'lara konmaz.
- Loglama yalnızca `AddPortfolioLogging` ile yapılır (redaction).
- Commit ve push öncesinde `scripts/scan-secrets.ps1` çalıştırılır.
