using Binance.API.Csharp.Client;
using Binance.API.Csharp.Client.Models.Enums;
using Binance.API.Csharp.Client.Models.Market;
using Binance.API.Csharp.Client.Utils;
using Microsoft.Practices.EnterpriseLibrary.Data;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net.Mail;
using System.Text;
using Trady.Analysis.Indicator;
using Trady.Core;
using Trady.Core.Infrastructure;

namespace Avci
{
    class Program
    {
        private static string mail_Adresleri = "Rapor maili almak istediğiniz mail adresini giriniz";

        public static List<RehberTablo> RehberTabloList;
        public static List<string> StratejiyeTakilanlarList = new List<string>();
        public static List<StratejiyeTakilan> StratejiyeTakilanlarTabloList = new List<StratejiyeTakilan>();
        public static List<string> HataLogList = new List<string>();

        private static readonly DateTime StartUnixTime = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        static void Main(string[] args)
        {
            Console.BufferHeight = Int16.MaxValue - 1;
            var baslangic = DateTime.Now;

            var binanceClient = new BinanceClient(
                new ApiClient(
                    "apiKey",
                    "apiSecret"
                ));

            RehberTabloGetir(binanceClient);
            Tarayici(binanceClient);
            IslemAcilisKaydet();
            StratejiyeTakilanKaydet();

            //mail_Adresleri ve MailGonderAsync alanlarındaki mail alanlarını doldurduktan sonra bu kısmı aktif ediniz.
            //Raporla(baslangic, DateTime.Now);
        }

        private static void RehberTabloGetir(BinanceClient binanceClient)
        {
            RehberTabloList = new List<RehberTablo>();

            DatabaseProviderFactory factory = new DatabaseProviderFactory();
            Database db = factory.Create("connStrBot");

            //Burada kendi stratejime göre başarılı bulduğum senaryoyu yazdım fakat kendinize göre sorguyu revize ediniz.
            var coms = db.GetSqlStringCommand("SELECT * FROM RehberTablo WHERE KarOran> 2 and BasariliIslemSayisi>1 and CAST(BasariliIslemSayisi as decimal(6,2))/CAST(IslemSayisi as decimal(6,2))>0.3  AND Sembol NOT IN (select it.Sembol from IslemTablosu it where it.IslemKapandiMi=0) ORDER BY Sembol,Periyot");
            var rehberTablo = db.ExecuteDataSet(coms);

            //Rehber tabloyu doldurduğunuz parite veye sembole göre where sorgusunu değişebilirsiniz.
            var tickerPrices = binanceClient.GetAllPrices().Result
                .Where(x => x.Symbol.EndsWith("USDT"))
                .Select(i => new SymbolPrice { Symbol = i.Symbol, Price = i.Price })
                .OrderBy(x => x.Symbol).ToList();

            foreach (DataRow drRehber in rehberTablo.Tables[0].Rows)
            {
                var mostParcala = drRehber["MostParametreleri"].ToString().Replace("(", "").Replace(")", "");

                RehberTabloList.Add(new RehberTablo
                {
                    Sembol = new SymbolPrice { Symbol = drRehber["Sembol"].ToString(), Price = tickerPrices.First(x => x.Symbol == drRehber["Sembol"].ToString()).Price },
                    Periyot = ZamanEnumDon(drRehber["Periyot"].ToString()),
                    MostParametreleri = new KeyValuePair<int, int>(Convert.ToInt32(mostParcala.Split(',')[0]),
                        Convert.ToInt32(mostParcala.Split(',')[1]))
                });
            }
        }

        private static void Tarayici(BinanceClient binanceClient)
        {
            TempSil();

            KeyValuePair<string, Candlestick[]> periyotKandilleri = new KeyValuePair<string, Candlestick[]>();

            foreach (var rehberSatir in RehberTabloList)
            {
                try
                {
                    Candlestick[] candlesticks;
                    // Aynı sembolün aynı periyotu için tekrar tekrar veri çekmeyip eldeki bar verileri ile işlem yapılıyor
                    if (periyotKandilleri.Key == rehberSatir.Sembol.Symbol + rehberSatir.Periyot.GetDescription())
                    {
                        candlesticks = periyotKandilleri.Value;
                    }
                    else
                    {
                        var candlestick = binanceClient.GetCandleSticks(rehberSatir.Sembol.Symbol, rehberSatir.Periyot, limit: 500).Result;
                        candlesticks = candlestick as Candlestick[] ?? candlestick.Where(x => StartUnixTime.AddMilliseconds(x.CloseTime).ToUniversalTime() < DateTime.Now.ToUniversalTime()).ToArray();
                        periyotKandilleri = new KeyValuePair<string, Candlestick[]>(rehberSatir.Sembol.Symbol + rehberSatir.Periyot.GetDescription(), candlesticks);
                    }

                    if (candlesticks.Length <= 360) continue;

                    List<IOhlcv> tradyCandles = candlesticks.Select(candle => new Candle(StartUnixTime.AddMilliseconds(candle.OpenTime).ToUniversalTime(), candle.Open, candle.High, candle.Low, candle.Close, candle.Volume)).Cast<IOhlcv>().ToList();

                    var mostListe = MostDLL.MostHesapla.Hesapla(tradyCandles, rehberSatir.MostParametreleri.Key, Convert.ToDouble((double)rehberSatir.MostParametreleri.Value / 100));
                    var iftsListe = InverseFisherTransformOnStochastic.IftsHesapla.Hesapla(tradyCandles, 5, 9);
                    var macdListe = MacdHesapla(tradyCandles);

                    List<IslemListe> islemListe = StratejiSonucHesapla(rehberSatir.Sembol, rehberSatir.Periyot, rehberSatir.MostParametreleri.Key, Convert.ToDouble((double)rehberSatir.MostParametreleri.Value / 100), mostListe, iftsListe, macdListe);
                    if (islemListe == null) continue;

                    if (islemListe.Last().Durum == "AL")
                    {
                        var sonAlSinyalKaydi = SonSinyalDegisimiDon(islemListe);
                        var ozet = OzetHesapla(islemListe, candlesticks.Length);

                        ozet.SonDurum = "AL";
                        ozet.AlBarAcilisTarihi = sonAlSinyalKaydi.AcilisZamani;
                        ozet.GecenBarSayisi = islemListe.Last().IslemIndex - sonAlSinyalKaydi.IslemIndex;
                        ozet.AlSinyalFiyat = sonAlSinyalKaydi.Fiyat;
                        ozet.MevcutFiyat = rehberSatir.Sembol.Price;
                        ozet.AlVeMevcutFarkOran = (rehberSatir.Sembol.Price - sonAlSinyalKaydi.Fiyat) / sonAlSinyalKaydi.Fiyat * 100;

                        if (StratejiAlOnay(iftsListe, macdListe, ozet.MevcutFiyat, ozet.GecenBarSayisi, ozet.AlSinyalFiyat, ozet.Sembol + " - " + ozet.Periyot + " - MOST" + ozet.MostParametreleri))
                        {
                            Console.WriteLine(ozet.Sembol + " - " + ozet.Periyot + " - MOST" + ozet.MostParametreleri + " işlemi açıldı!");
                            TempKaydet(ozet);
                        }
                        else
                        {
                            Console.WriteLine(ozet.Sembol + " - " + ozet.Periyot + " - MOST" + ozet.MostParametreleri + " AL durumunda ama strateji onayı alamadı");
                        }
                    }
                    else
                    {
                        Console.WriteLine(islemListe.Last().Sembol + " - " + islemListe.Last().Periyot + " - MOST(" + islemListe.Last().MostParametreleri + ") stratejiye göre henüz SAT durumunda)");
                    }
                }
                catch (Exception e)
                {
                    HataLogList.Add("Tarayici Hatası: " + rehberSatir.Sembol.Symbol + " - " + rehberSatir.Periyot.GetDescription());
                    Console.WriteLine(e);
                }
            }

            SonucKaydet();
        }

        private static void IslemAcilisKaydet()
        {
            try
            {
                DatabaseProviderFactory factory = new DatabaseProviderFactory();
                Database db = factory.Create("connStrBot");
                var coms = db.GetSqlStringCommand("SELECT * FROM TarayiciSonuclari WHERE Sembol NOT IN (SELECT Sembol FROM IslemTablosu WHERE IslemKapandiMi=0)");
                DataSet _tarayicidanIslemciyeAktarilanlar = db.ExecuteDataSet(coms);

                if (_tarayicidanIslemciyeAktarilanlar.Tables.Count > 0)
                {
                    foreach (DataRow aktarilacak in _tarayicidanIslemciyeAktarilanlar.Tables[0].Rows)
                    {
                        var mevcutFiyat = Convert.ToDecimal(aktarilacak["MevcutFiyat"]);
                        var coinAdet = 1 / mevcutFiyat;

                        var comm = db.GetSqlStringCommand("INSERT INTO IslemTablosu (Sembol,Periyot,MostParametreleri,GirisTarihi,GirisBarAcilisTarihi,CoinAdeti,GirisFiyat,GirisPariteKarsiligi,KontrolTarihi,KontrolFiyat,KontrolPariteKarsiligi,KarPariteKarsiligi,KarOrani,PikFiyat,PikOrani,PikTarihi,IslemKapandiMi) VALUES (@Sembol,@Periyot,@MostParametreleri,@GirisTarihi,@GirisBarAcilisTarihi,@CoinAdeti,@GirisFiyat,@GirisPariteKarsiligi,@KontrolTarihi,@KontrolFiyat,@KontrolPariteKarsiligi,@KarPariteKarsiligi,@KarOrani,@PikFiyat,@PikOrani,@PikTarihi,@IslemKapandiMi)");
                        db.AddInParameter(comm, "Sembol", DbType.String, aktarilacak["Sembol"]);
                        db.AddInParameter(comm, "Periyot", DbType.String, aktarilacak["Periyot"]);
                        db.AddInParameter(comm, "MostParametreleri", DbType.String, aktarilacak["MostParametreleri"]);
                        db.AddInParameter(comm, "GirisTarihi", DbType.DateTime, DateTime.Now);
                        db.AddInParameter(comm, "GirisBarAcilisTarihi", DbType.DateTime, Convert.ToDateTime(aktarilacak["AlBarAcilisTarihi"]));
                        db.AddInParameter(comm, "CoinAdeti", DbType.Decimal, coinAdet);
                        db.AddInParameter(comm, "GirisFiyat", DbType.Decimal, mevcutFiyat);
                        db.AddInParameter(comm, "GirisPariteKarsiligi", DbType.Decimal, Convert.ToDecimal(1));
                        db.AddInParameter(comm, "KontrolTarihi", DbType.DateTime, DateTime.Now);
                        db.AddInParameter(comm, "KontrolFiyat", DbType.Decimal, mevcutFiyat);
                        db.AddInParameter(comm, "KontrolPariteKarsiligi", DbType.Decimal, Convert.ToDecimal(1));
                        db.AddInParameter(comm, "KarPariteKarsiligi", DbType.Decimal, Convert.ToDecimal(0));
                        db.AddInParameter(comm, "KarOrani", DbType.Decimal, Convert.ToDecimal(0));
                        db.AddInParameter(comm, "PikFiyat", DbType.Decimal, mevcutFiyat);
                        db.AddInParameter(comm, "PikOrani", DbType.Decimal, Convert.ToDecimal(0));
                        db.AddInParameter(comm, "PikTarihi", DbType.DateTime, DateTime.Now);
                        db.AddInParameter(comm, "IslemKapandiMi", DbType.Int32, 0);

                        db.ExecuteNonQuery(comm);


                        //Borsadaki alım işlemini burada yapabilirsiniz
                        //Doküman:
                        //https://github.com/melihtuna/Binance.API.Csharp.Client/blob/master/Documentation/AccountMethods.md
                    }
                }
            }
            catch (Exception e)
            {
                HataLogList.Add("TarayicidanIslemciyeAktar Hata: " + e.Message);
                Console.WriteLine(e);
            }
        }

        private static List<decimal> MacdHesapla(List<IOhlcv> tradyCandles)
        {
            if (tradyCandles.Count > 360)
            {
                var mcdList = new MovingAverageConvergenceDivergenceHistogram(tradyCandles, 12, 26, 9);
                var hesapla = mcdList.Compute();

                var sonuc = hesapla.Select(x => Convert.ToDecimal(x.Tick)).ToList();
                sonuc.RemoveRange(0, 140);
                return sonuc;
            }

            return null;
        }

        private static List<IslemListe> StratejiSonucHesapla(SymbolPrice coin, TimeInterval periyot, int emaUzunluk, double yuzde, List<MostDLL.Most> mostListe, List<double> iftsListe, List<decimal> macdListe)
        {
            List<IslemListe> islemListe = new List<IslemListe>();
            var sgIslemSay = 0;
            var sgPozisyon = "SAT";

            for (int i = 4; i < mostListe.Count; i++)
            {
                var itemMost = mostListe[i];
                if (itemMost.MostDurum == "AL")
                {
                    if (sgPozisyon == "SAT")
                    {
                        var iftsOnay = iftsListe[i - 1] >= -0.5 && iftsListe[i - 2] < -0.5 && iftsListe[i - 3] < -0.5;
                        var macdOnay = macdListe[i - 1] > 0;

                        if (iftsOnay && macdOnay)
                        {
                            sgIslemSay++;
                            sgPozisyon = "AL";
                        }
                    }
                }
                else
                {
                    if (sgPozisyon == "AL")
                    {
                        sgPozisyon = "SAT";
                    }
                }

                DateTime utc = itemMost.Bar.DateTime.UtcDateTime;
                islemListe.Add(new IslemListe
                {
                    Sembol = coin.Symbol,
                    MostParametreleri = "(" + emaUzunluk + "," + yuzde * 100 + ")",
                    Periyot = periyot.GetDescription(),
                    AcilisZamani = utc.ToLocalTime(),
                    Durum = sgPozisyon,
                    EmaDeger = itemMost.EmaDegeri,
                    Fiyat = itemMost.Bar.Open,
                    MostDeger = itemMost.MostDegeri,
                    IslemSayisi = sgIslemSay,
                    Bar = itemMost.Bar
                });
            }

            return islemListe;
        }

        private static bool StratejiAlOnay(List<double> iftsListe, List<decimal> macdListe, decimal barGuncelFiyat, int gecenBarSayisi, decimal alSinyalFiyat, string LogMetni = "")
        {
            var iftsOnay = iftsListe[iftsListe.Count - 2] >= -0.5 && iftsListe[iftsListe.Count - 3] < -0.5 && iftsListe[iftsListe.Count - 4] < -0.5;
            var macdOnay = macdListe[macdListe.Count - 2] > 0;

            if (iftsOnay)
            {
                if (macdOnay)
                {
                    return true;
                }

                if (LogMetni != "")
                {
                    StratejiyeTakilanlarList.Add(LogMetni + " MACD onayı alamadı");
                    StratejiyeTakilanlarTabloList.Add(new StratejiyeTakilan
                    {
                        Sembol = LogMetni.Split('-')[0].Trim(),
                        Periyot = LogMetni.Split('-')[1].Trim(),
                        MostParametreleri = LogMetni.Split('-')[2].Trim(),
                        AlSinyalFiyat = alSinyalFiyat,
                        GecenBarSayisi = gecenBarSayisi,
                        MevcutFiyat = barGuncelFiyat,
                        Sebep = "MACD onayı alamadı"
                    });
                }
            }
            else
            {
                if (LogMetni != "")
                {
                    StratejiyeTakilanlarList.Add(LogMetni + " IFTSCTOCH onayı alamadı");
                    StratejiyeTakilanlarTabloList.Add(new StratejiyeTakilan
                    {
                        Sembol = LogMetni.Split('-')[0].Trim(),
                        Periyot = LogMetni.Split('-')[1].Trim(),
                        MostParametreleri = LogMetni.Split('-')[2].Trim(),
                        AlSinyalFiyat = alSinyalFiyat,
                        GecenBarSayisi = gecenBarSayisi,
                        MevcutFiyat = barGuncelFiyat,
                        Sebep = "IFTSCTOCH onayı alamadı"
                    });
                }
            }

            return false;
        }

        private static IslemOzet OzetHesapla(List<IslemListe> islemListe, int barSayisi)
        {
            decimal coinAdet = 0;
            decimal sermaye = 100;
            decimal sunuc = 100;

            bool ilkIslemeGirildiMi = false;
            var durum = islemListe.First().Durum;

            //Aşağıdaki işlemlerde /1000 hesaplaması, Binance'ın alım-satım işlemlerinde 0.01% komisyon farkıdır. Farklı oranınız varsa değiştirebilirsiniz.

            foreach (var islem in islemListe)
            {
                if (durum != islem.Durum)
                {
                    if (islem.Durum == "AL")
                    {
                        coinAdet = sunuc / islem.Fiyat;
                        coinAdet = coinAdet - (coinAdet / 1000);
                        ilkIslemeGirildiMi = true;
                    }
                    else
                    {
                        if (ilkIslemeGirildiMi)
                        {
                            sunuc = islem.Fiyat * coinAdet;
                            sunuc = sunuc - (sunuc / 1000);
                        }
                    }

                    durum = islem.Durum;
                }
            }

            var kar = islemListe.Last().IslemSayisi > 0 ? sunuc - sermaye : 0;
            var karOrani = islemListe.Last().IslemSayisi > 0 ? (sunuc - sermaye) / sermaye * 100 : 0;

            return new IslemOzet
            {
                Sembol = islemListe.First().Sembol,
                Kar = kar,
                Sermaye = sermaye,
                MostParametreleri = islemListe.First().MostParametreleri,
                Periyot = islemListe.First().Periyot,
                KarOran = karOrani,
                BarSayisi = barSayisi,
                IslemSayisi = islemListe.Last().IslemSayisi
            };
        }

        private static IslemListe SonSinyalDegisimiDon(List<IslemListe> islemListe)
        {
            var islem = islemListe.First();
            var durum = islemListe.First().Durum;

            foreach (var t in islemListe)
            {
                if (t.Durum != durum)
                {
                    durum = t.Durum;
                    islem = t;
                }
            }

            return islem;
        }

        private static void TempSil()
        {
            try
            {
                DatabaseProviderFactory factory = new DatabaseProviderFactory();
                Database db = factory.Create("connStrBot");
                var comm = db.GetSqlStringCommand("TRUNCATE TABLE temp_TarayiciSonuclari");
                db.ExecuteNonQuery(comm);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }

        private static void TempKaydet(IslemOzet islemOzet)
        {
            try
            {
                DatabaseProviderFactory factory = new DatabaseProviderFactory();
                Database db = factory.Create("connStrBot");
                var comm = db.GetSqlStringCommand("INSERT INTO temp_TarayiciSonuclari (Sembol,Periyot,MostParametreleri,Sermaye,Kar,KarOran,BarSayisi,IslemSayisi,SonDurum,AlBarAcilisTarihi,GecenBarSayisi,AlSinyalFiyat,MevcutFiyat,AlVeMevcutFarkOran) VALUES (@Sembol,@Periyot,@MostParametreleri,@Sermaye,@Kar,@KarOran,@BarSayisi,@IslemSayisi,@SonDurum,@AlBarAcilisTarihi,@GecenBarSayisi,@AlSinyalFiyat,@MevcutFiyat,@AlVeMevcutFarkOran)");
                db.AddInParameter(comm, "Sembol", DbType.String, islemOzet.Sembol);
                db.AddInParameter(comm, "Periyot", DbType.String, islemOzet.Periyot);
                db.AddInParameter(comm, "MostParametreleri", DbType.String, islemOzet.MostParametreleri);
                db.AddInParameter(comm, "Sermaye", DbType.Decimal, islemOzet.Sermaye);
                db.AddInParameter(comm, "Kar", DbType.Decimal, islemOzet.Kar);
                db.AddInParameter(comm, "KarOran", DbType.Decimal, islemOzet.KarOran);
                db.AddInParameter(comm, "BarSayisi", DbType.Int32, islemOzet.BarSayisi);
                db.AddInParameter(comm, "IslemSayisi", DbType.Int32, islemOzet.IslemSayisi);
                db.AddInParameter(comm, "SonDurum", DbType.String, islemOzet.SonDurum);
                db.AddInParameter(comm, "AlBarAcilisTarihi", DbType.DateTime, islemOzet.AlBarAcilisTarihi);
                db.AddInParameter(comm, "GecenBarSayisi", DbType.Int32, islemOzet.GecenBarSayisi);
                db.AddInParameter(comm, "AlSinyalFiyat", DbType.Decimal, islemOzet.AlSinyalFiyat);
                db.AddInParameter(comm, "MevcutFiyat", DbType.Decimal, islemOzet.MevcutFiyat);
                db.AddInParameter(comm, "AlVeMevcutFarkOran", DbType.Decimal, islemOzet.AlVeMevcutFarkOran);
                db.ExecuteNonQuery(comm);

                //_kayitSayisi++;
            }
            catch (Exception e)
            {
                HataLogList.Add("TempKaydet Hata: " + e.Message);
                Console.WriteLine(e);
            }
        }

        private static void SonucKaydet()
        {
            try
            {
                //işlemci robotu için tablo oluşunca, semboller arasına işlem tablosundaki açık pozisyonlu birimleri sokmamak gerekecek ilgili ayrımı yaptırmayı unutma!

                DatabaseProviderFactory factory = new DatabaseProviderFactory();
                Database db = factory.Create("connStrBot");
                var query = @"SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
                            DECLARE Karli_Sec_Cursor CURSOR FOR
                            SELECT Sembol FROM temp_TarayiciSonuclari
                            GROUP BY Sembol
                            UNION
                            SELECT Sembol FROM IslemTablosu
                            WHERE IslemKapandiMi=0
                            GROUP BY Sembol

                            DECLARE	@Sembol NVARCHAR(50);

                            TRUNCATE TABLE TarayiciSonuclari
                            OPEN Karli_Sec_Cursor
                            FETCH NEXT FROM Karli_Sec_Cursor INTO @Sembol
                            WHILE @@FETCH_STATUS =0
	                            BEGIN
	                            INSERT INTO TarayiciSonuclari 
	                            SELECT TOP 1 Sembol, Periyot, MostParametreleri, Sermaye, Kar, KarOran, BarSayisi, IslemSayisi, SonDurum, AlBarAcilisTarihi, GecenBarSayisi, AlSinyalFiyat, MevcutFiyat, AlVeMevcutFarkOran 
	                            FROM temp_TarayiciSonuclari
	                            WHERE Sembol=@Sembol
	                            ORDER BY KarOran DESC
		                            FETCH NEXT FROM Karli_Sec_Cursor INTO @Sembol
	                            END
                            CLOSE Karli_Sec_Cursor
                            DEALLOCATE Karli_Sec_Cursor
                            SELECT * FROM TarayiciSonuclari ORDER BY KarOran DESC;";

                var comm = db.GetSqlStringCommand(query);
                db.ExecuteNonQuery(comm);
            }
            catch (Exception e)
            {
                HataLogList.Add("SonucKaydet Hata: " + e.Message);
                Console.WriteLine(e);
            }
        }

        private static TimeInterval ZamanEnumDon(string periyot)
        {
            switch (periyot)
            {
                case "1m":
                    return TimeInterval.Minutes_1;
                case "3m":
                    return TimeInterval.Minutes_3;
                case "5m":
                    return TimeInterval.Minutes_5;
                case "15m":
                    return TimeInterval.Minutes_15;
                case "30m":
                    return TimeInterval.Minutes_30;
                case "1h":
                    return TimeInterval.Hours_1;
                case "2h":
                    return TimeInterval.Hours_2;
                case "4h":
                    return TimeInterval.Hours_4;
                case "6h":
                    return TimeInterval.Hours_6;
                case "8h":
                    return TimeInterval.Hours_8;
                case "12h":
                    return TimeInterval.Hours_12;
                case "1d":
                    return TimeInterval.Days_1;
                case "3d":
                    return TimeInterval.Days_3;
                case "1w":
                    return TimeInterval.Weeks_1;
                case "1M":
                    return TimeInterval.Months_1;
            }

            return TimeInterval.Days_1;
        }

        private static void Raporla(DateTime baslangic, DateTime bitis)
        {
            StringBuilder sb = new StringBuilder();

            sb.Append("<p>Başlangıç: " + baslangic + " | Bitiş: " + bitis + " | İşlem Süresi: " +
                        $"{(bitis - baslangic).TotalMinutes:N0}" + " dakika</p>");

            DatabaseProviderFactory factory = new DatabaseProviderFactory();
            Database db = factory.Create("connStrBot");

            //yeni açılan pozisyonlar
            var coma = db.GetSqlStringCommand("SELECT * FROM IslemTablosu WHERE IslemKapandiMi=0 and GirisTarihi>DATEADD(MINUTE,-4,GETDATE()) ORDER BY Sembol");
            var acilanPozisyonlarDataSet = db.ExecuteDataSet(coma);
            if (acilanPozisyonlarDataSet.Tables.Count > 0 && acilanPozisyonlarDataSet.Tables[0].Rows.Count > 0)
            {
                sb.Append("<br/><h3>YENİ AÇILAN POZİSYONLAR (" + acilanPozisyonlarDataSet.Tables[0].Rows.Count + " ADET) : </h3>");

                sb.Append("<table border='1' bordercolor='black' cellpadding='3' cellspacing='0'>");
                sb.Append(RaporTabloBasliklar());

                foreach (DataRow dr in acilanPozisyonlarDataSet.Tables[0].Rows)
                {
                    sb.Append(RaporTabloSatir(dr));
                }

                sb.Append("</table>");
            }

            //tüm açık pozisyonlar
            var comt = db.GetSqlStringCommand("SELECT * FROM IslemTablosu WHERE IslemKapandiMi=0 ORDER BY Sembol");
            var tumAcikPozisyonlarDataSet = db.ExecuteDataSet(comt);
            if (tumAcikPozisyonlarDataSet.Tables.Count > 0 && tumAcikPozisyonlarDataSet.Tables[0].Rows.Count > 0)
            {
                sb.Append("<br/><h3>TÜM AÇIK POZİSYONLAR (" + tumAcikPozisyonlarDataSet.Tables[0].Rows.Count + " ADET) : </h3>");

                sb.Append("<table border='1' bordercolor='black' cellpadding='3' cellspacing='0'>");
                sb.Append(RaporTabloBasliklar());
                foreach (DataRow dr in tumAcikPozisyonlarDataSet.Tables[0].Rows)
                {
                    sb.Append(RaporTabloSatir(dr));
                }

                sb.Append("</table>");
            }

            //kar zarar rapor 
            var comr = db.GetSqlStringCommand(@"select (select AVG(KarOrani) from IslemTablosu where IslemKapandiMi = 1) KapananIslemKarOran, 
            (select COUNT(*) from IslemTablosu where IslemKapandiMi = 1) KapananIslemSayisi, 
            (select AVG(KarOrani) from IslemTablosu where IslemKapandiMi = 0) AcikIslemKarOran, 
            (select COUNT(*) from IslemTablosu where IslemKapandiMi = 0) AcikIslemSayisi,
            AVG(KarOrani) TumIslemlerKarOran,
            COUNT(*) TumIslemlerinSayisi
            from IslemTablosu");
            var genelToplamlarDataSet = db.ExecuteDataSet(comr);
            if (genelToplamlarDataSet.Tables.Count > 0 && genelToplamlarDataSet.Tables[0].Rows.Count > 0)
            {
                sb.Append("<br/><h3>GENEL DURUM</h3><br/>");

                sb.Append("<table border='1' bordercolor='black' cellpadding='3' cellspacing='0'>");
                sb.Append("<tr>");
                sb.Append("<td>Kapanan İşlem Kar Oranı</td>");
                sb.Append("<td>Kapanan İşlem Sayısı</td>");
                sb.Append("<td>Açık İşlem Kar Oranı</td>");
                sb.Append("<td>Açık İşlem Sayısı</td>");
                sb.Append("<td>Tüm İşlemler Kar Oranı</td>");
                sb.Append("<td>Tüm İşlemlerin Sayısı</td>");
                sb.Append("</tr>");

                sb.Append("<tr>");
                sb.Append("<td>" + genelToplamlarDataSet.Tables[0].Rows[0]["KapananIslemKarOran"] + "</td>");
                sb.Append("<td>" + genelToplamlarDataSet.Tables[0].Rows[0]["KapananIslemSayisi"] + "</td>");
                sb.Append("<td>" + genelToplamlarDataSet.Tables[0].Rows[0]["AcikIslemKarOran"] + "</td>");
                sb.Append("<td>" + genelToplamlarDataSet.Tables[0].Rows[0]["AcikIslemSayisi"] + "</td>");
                sb.Append("<td>" + genelToplamlarDataSet.Tables[0].Rows[0]["TumIslemlerKarOran"] + "</td>");
                sb.Append("<td>" + genelToplamlarDataSet.Tables[0].Rows[0]["TumIslemlerinSayisi"] + "</td>");
                sb.Append("</tr>");

                sb.Append("</table>");
            }

            string mailListesi = mail_Adresleri;
            string[] alicilar = mailListesi.Split(',');
            List<string> liste = alicilar.ToList();

            MailGonderAsync(liste, "Avcı Robotu - " + baslangic.ToString("dd-MM-yyyy HH:mm"), sb.ToString(), System.Net.Mail.MailPriority.High);
        }

        public static void StratejiyeTakilanKaydet()
        {
            try
            {
                StratejiyeTakilanTemizle();

                for (int i = 0; i < StratejiyeTakilanlarTabloList.Count; i++)
                {
                    DatabaseProviderFactory factory = new DatabaseProviderFactory();
                    Database db = factory.Create("connStrBot");
                    var comm = db.GetSqlStringCommand("INSERT INTO StratejiyeTakilanlar (Sembol, Periyot, MostParametreleri, GecenBarSayisi, AlSinyalFiyat, MevcutFiyat, Sebep) VALUES (@Sembol, @Periyot, @MostParametreleri, @GecenBarSayisi, @AlSinyalFiyat, @MevcutFiyat, @Sebep)");
                    db.AddInParameter(comm, "@Sembol", DbType.String, StratejiyeTakilanlarTabloList[i].Sembol);
                    db.AddInParameter(comm, "@Periyot", DbType.String, StratejiyeTakilanlarTabloList[i].Periyot);
                    db.AddInParameter(comm, "@MostParametreleri", DbType.String, StratejiyeTakilanlarTabloList[i].MostParametreleri.Replace("MOST", ""));
                    db.AddInParameter(comm, "@GecenBarSayisi", DbType.Int32, StratejiyeTakilanlarTabloList[i].GecenBarSayisi);
                    db.AddInParameter(comm, "@AlSinyalFiyat", DbType.Decimal, StratejiyeTakilanlarTabloList[i].AlSinyalFiyat);
                    db.AddInParameter(comm, "@MevcutFiyat", DbType.Decimal, StratejiyeTakilanlarTabloList[i].MevcutFiyat);
                    db.AddInParameter(comm, "@Sebep", DbType.String, StratejiyeTakilanlarTabloList[i].Sebep);


                    db.ExecuteNonQuery(comm);
                }
            }
            catch (Exception e)
            {
                HataLogList.Add("IslemDetayKaydet Hata: " + e.Message);
                Console.WriteLine(e);
            }
        }

        public static void StratejiyeTakilanTemizle()
        {
            try
            {
                DatabaseProviderFactory factory = new DatabaseProviderFactory();
                Database db = factory.Create("connStrBot");
                var comm = db.GetSqlStringCommand("TRUNCATE TABLE StratejiyeTakilanlar");
                db.ExecuteNonQuery(comm);
            }
            catch (Exception e)
            {
                HataLogList.Add("StratejiyeTakilanTemizle Hata: " + e.Message);
                Console.WriteLine(e);
            }
        }

        public static string RaporTabloBasliklar()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<tr>");
            sb.Append("<td>Sembol</td>");
            sb.Append("<td>Periyot</td>");
            sb.Append("<td>Most</td>");
            sb.Append("<td>Giriş Tarihi</td>");
            sb.Append("<td>Giriş Fiyatı</td>");
            sb.Append("<td>Kontrol Tarihi</td>");
            sb.Append("<td>Kontrol Fiyatı</td>");
            sb.Append("<td>Kar Oranı (%)</td>");
            sb.Append("</tr>");
            return sb.ToString();
        }

        public static string RaporTabloSatir(DataRow dr)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<tr>");
            sb.Append("<td>" + dr["Sembol"] + "</td>");
            sb.Append("<td>" + dr["Periyot"] + "</td>");
            sb.Append("<td>Most" + dr["MostParametreleri"] + "</td>");
            sb.Append("<td>" + Convert.ToDateTime(dr["GirisTarihi"]).ToString("dd-MM-yyyy HH:mm:ss") + "</td>");
            sb.Append("<td>" + dr["GirisFiyat"] + "</td>");
            sb.Append("<td>" + Convert.ToDateTime(dr["KontrolTarihi"]).ToString("dd-MM-yyyy HH:mm:ss") + "</td>");
            sb.Append("<td>" + dr["KontrolFiyat"] + "</td>");
            sb.Append("<td>" + dr["KarOrani"] + "</td>");
            sb.Append("</tr>");
            return sb.ToString();
        }

        public static void MailGonderAsync(List<string> aliciMailAdresleri, string epostaKonusu, string epostaIcerigi, MailPriority oncelikSeviyesi)
        {
            try
            {
                int port = 587;
                string sunucu = "smtp.gmail.com";
                string sifre = "Gmail Şifreniz";
                string kullaniciAd = "Gmail Adresiniz";
                bool sslAktifMi = true;

                SmtpClient smtpClient = new SmtpClient(sunucu, port)
                {
                    Credentials = new System.Net.NetworkCredential(kullaniciAd, sifre),
                    EnableSsl = sslAktifMi
                };

                MailMessage mm = new MailMessage
                {
                    From = new MailAddress(kullaniciAd, epostaKonusu, Encoding.UTF8),
                    Priority = oncelikSeviyesi
                };

                foreach (var alici in aliciMailAdresleri)
                    mm.To.Add(alici);
                mm.SubjectEncoding = Encoding.GetEncoding("UTF-8");
                mm.BodyEncoding = Encoding.GetEncoding("UTF-8");
                mm.Subject = epostaKonusu;
                mm.Body = epostaIcerigi;
                mm.IsBodyHtml = true;
                smtpClient.Send(mm);

                Console.WriteLine("==================== BİTTİ ====================");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Hata" + ex);
            }
        }
    }

    public class IslemOzet
    {
        public string Sembol { get; set; }
        public string Periyot { get; set; }
        public string MostParametreleri { get; set; }
        public decimal Sermaye { get; set; }
        public decimal Kar { get; set; }
        public decimal KarOran { get; set; }
        public int BarSayisi { get; set; }
        public int IslemSayisi { get; set; }
        public string SonDurum { get; set; }
        public DateTime AlBarAcilisTarihi { get; set; }
        public int GecenBarSayisi { get; set; }
        public decimal AlSinyalFiyat { get; set; }
        public decimal MevcutFiyat { get; set; }
        public decimal AlVeMevcutFarkOran { get; set; }
    }

    public class IslemListe
    {
        public int IslemIndex { get; set; }
        public string Sembol { get; set; }
        public string Periyot { get; set; }
        public string MostParametreleri { get; set; }
        public DateTime AcilisZamani { get; set; }
        public decimal MostDeger { get; set; }
        public decimal EmaDeger { get; set; }
        public string Durum { get; set; }
        public decimal Fiyat { get; set; }
        public int IslemSayisi { get; set; }
        public IOhlcv Bar { get; set; }
    }

    public class RehberTablo
    {
        public SymbolPrice Sembol { get; set; }
        public TimeInterval Periyot { get; set; }
        public KeyValuePair<int, int> MostParametreleri { get; set; }
    }

    public class StratejiyeTakilan
    {
        public string Sembol { get; set; }
        public string Periyot { get; set; }
        public string MostParametreleri { get; set; }
        public int GecenBarSayisi { get; set; }
        public decimal AlSinyalFiyat { get; set; }
        public decimal MevcutFiyat { get; set; }
        public string Sebep { get; set; }
    }
}
