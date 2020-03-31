#Trade Bot Ekosistemim

Bu projede birbiri ile ilişkili robotlar aracılığı ile tarama, alım durumu ve stop takibi yapan işlemlerinin örneğini vereceğim. İşlemlerde kullanılan veri tabanını kurabileceğiniz MsSQL Scriptini de ek olarak ekliyorum. ConnectionString ayarlarınızı kendiniz yapmalısınız.

1 — Tarayıcı: İstediğim paritede istediğim periyotlar için stratejimi tüm sembollerde çalıştırarak backtest sonucunu kaydeden robottur. Bu robotun oluşturduğu rehber tablo işleme girip girmeyeceğim parametreleri belirler. Şu anda Binance üzerinde USDT için 1 saatlik periyotta tarama yapması 6 dakika sürüyor ve her yarım saatte bir kez çalışacak şekilde ayarlı durumda. Bu süre içerisinde yaklaşık 4500 backtest yapıyor. Bu taramayı elle yapmayı hayal edemeyiz sanırım.

2 — Avcı: Oluşan rehber tablo sonuçlarından stratejime bağlı olarak başarılı sayılan kayıtları alarak, şu anda alınabilir mi diye kontrol eder; alınabilir gördüğü işlemleri açar. Bu robot her 5 dakikada bir çalışır ve çalışma süresi yaklaşık 30 saniye sürer.

3 — Tüccar: Açık olan işlemleri her dakika başında kontrol ederek stop olması gereken var mı kontrolü yapar. Buradaki stop’tan kasıt sadece ani düşüş yaşayan işlemler için değil, stratejime göre sat durumuna geçmiş işlemleri de kapsıyor. Robotun çalışması yaklaşık 10 saniye sürüyor.

Ekosistemle ilgili detaylı anlatımı aşağıdan bulabilirsiniz: https://medium.com/@a.melihtuna/trade-bot-ekosistemim-c1698177272c
