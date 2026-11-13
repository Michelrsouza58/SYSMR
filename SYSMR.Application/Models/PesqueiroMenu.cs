namespace SYSMR.Application.Models;

public static class PesqueiroMenu
{
    public static IReadOnlyList<RestaurantMenuItemRecord> Create() =>
        CreateItems().Select(item => item with { Description = Descriptions.GetValueOrDefault(item.Name, "") }).ToArray();

    private static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>
    {
        ["Tilápia"] = "Filé de tilápia, sem espinho.",
        ["Pacu"] = "Costelinha de pacu, com espinho.",
        ["Porquim"] = "Filé de porquim, sem espinho.",
        ["Pintado"] = "Filé de pintado, sem espinho.",
        ["Lambari"] = "Costelinha de lambari, bem crocante.",
        ["Peixe ao molho"] = "Pangasus ao molho.",
        ["Batata tradicional"] = "Batata palito frita.",
        ["Batata especial"] = "Coberta com cheddar e bacon.",
        ["Torresmo"] = "Torresmo de porco.",
        ["Porco com mandioca"] = "Carne de porco em cubos com mandioca frita.",
        ["Cebola"] = "Anéis de cebola frito.",
        ["Calabresa"] = "Linguiça calabresa.",
        ["Mandioca"] = "Mandioca frita em cubos.",
        ["Pimenta recheada - unidade"] = "Pimenta dedo de moça, empanado e recheado. Carne seca, moída, bacalhau, calabresa e queijo.",
        ["Pimenta recheada - porção G"] = "Pimenta dedo de moça, empanado e recheado. Carne seca, moída, bacalhau, calabresa e queijo.",
        ["Frango"] = "Tulipa de frango.",
        ["Parmegiana"] = "Filé de tilápia, sem espinho, molho pomarola e queijo, acom. arroz, salada e fritas.",
        ["Pirão"] = "Caldo de filé de peixe.",
        ["Salada"] = "Alface, tomate, pepino, beterraba, cenoura, azeitona e cebola.",
        ["Misto"] = "Salame, presunto e queijo, e azeitona.",
        ["Azeitona recheada"] = "Azeitona empanada, recheada. Carne ou queijo.",
        ["Medalhão"] = "Medalhão de tilápia com provolone; tilápia com bacon.",
        ["Bolinho"] = "Bolinho de pintado; bolinho de bacalhau.",
        ["Suco Pomar Citrus"] = "POMAR CITRUS",
        ["Coca-Cola 1 litro"] = "Coca cola e guaraná Antarctica.",
        ["Guaraná Antarctica 1 litro"] = "Coca cola e guaraná Antarctica.",
        ["Energético TNT"] = "Tnt e Redbull.",
        ["Energético Red Bull"] = "Tnt e Redbull.",
        ["H2O Limoneto"] = "Limoneto.",
        ["Cerveja Antarctica"] = "Antartica boa, Sub zero e Skol.",
        ["Cerveja Sub Zero"] = "Antartica boa, Sub zero e Skol.",
        ["Cerveja Skol"] = "Antartica boa, Sub zero e Skol.",
        ["Vinho Girola"] = "Garrafa de vinho Girola.",
        ["Caipirinha"] = "Limão, morango e maracujá.",
        ["Batida"] = "Morango, maracujá e abacaxi.",
        ["Drink"] = "Sex on the beach, gin flash, morango fest e X de anjo."
    };

    private static IReadOnlyList<RestaurantMenuItemRecord> CreateItems() =>
    [
        Food("Tilápia", 40m, 55m),
        Food("Pacu", 40m, 55m),
        Food("Porquim", 40m, 55m),
        Food("Pintado", 40m, 55m),
        Food("Lambari", null, 55m),
        Food("Peixe ao molho", 50m, 70m),
        Food("Batata tradicional", 32m, 45m),
        Food("Batata especial", 45m, 58m),
        Food("Torresmo", null, 38m),
        Food("Porco com mandioca", null, 55m),
        Food("Cebola", 28m, 40m),
        Food("Calabresa", 35m, 50m),
        Food("Mandioca", null, 26m),
        Single("Pimenta recheada - unidade", "Porções", 6m, "kitchen", "Carne seca", "Moída", "Bacalhau", "Calabresa", "Queijo"),
        Food("Pimenta recheada - porção G", null, 30m, "Carne seca", "Moída", "Bacalhau", "Calabresa", "Queijo"),
        Food("Frango", null, 55m),
        Food("Parmegiana", 89.99m, 110m),
        Food("Arroz", 8m, 12m),
        Food("Pirão", 10m, 15m),
        Food("Feijão", 5m, 8m),
        Food("Salada", 15m, 20m),
        Food("Azeitona", null, 15m),
        Food("Presunto e queijo", 30m, 40m),
        Food("Salame", null, 25m),
        Food("Misto", null, 50m),
        Food("Azeitona recheada", null, 30m, "Carne", "Queijo"),
        Food("Medalhão", null, 48m, "Tilápia com provolone", "Tilápia com bacon"),
        Food("Bolinho", null, 48m, "Pintado", "Bacalhau"),
        Food("Camarão", null, 75m),
        Drink("Suco Pomar Citrus", 18m),
        Drink("Coca-Cola 1 litro", 10m),
        Drink("Guaraná Antarctica 1 litro", 10m),
        Drink("Refrigerante lata", 6m),
        Drink("Skol lata", 6m),
        Drink("Antarctica lata", 6m),
        Drink("Original lata", 7m),
        Drink("Brahma Zero lata", 7m),
        Drink("Heineken Zero lata", 8m),
        Drink("Heineken lata", 8m),
        Drink("Energético TNT", 10m),
        Drink("Energético Red Bull", 10m),
        Drink("H2O Limoneto", 8m),
        Drink("Água sem gás", 3m),
        Drink("Água com gás", 4m),
        Drink("Cerveja Antarctica", 10m),
        Drink("Cerveja Sub Zero", 10m),
        Drink("Cerveja Skol", 10m),
        Drink("Antarctica Original", 12m),
        Drink("Heineken 600ml", 16m),
        Drink("Vinho Girola", 20m),
        Single("Caipirinha", "Bebidas", 20m, "bar", "Limão", "Morango", "Maracujá"),
        Single("Batida", "Bebidas", 24m, "bar", "Morango", "Maracujá", "Abacaxi"),
        Single("Drink", "Bebidas", 25m, "bar", "Sex on the beach", "Gin flash", "Morango fest", "X de anjo"),
        Drink("Heineken long neck", 9m)
    ];

    private static RestaurantMenuItemRecord Food(string name, decimal? medium, decimal large, params string[] options) =>
        new(Guid.NewGuid(), name, "Porções", medium ?? large, true, Options: Options(options),
            PrintTarget: "kitchen", PriceM: medium, PriceG: large);

    private static RestaurantMenuItemRecord Drink(string name, decimal price) =>
        Single(name, "Bebidas", price, "bar");

    private static RestaurantMenuItemRecord Single(string name, string category, decimal price, string target, params string[] options) =>
        new(Guid.NewGuid(), name, category, price, true, Options: Options(options), PrintTarget: target);

    private static IReadOnlyList<RestaurantMenuOptionRecord> Options(IEnumerable<string> names) =>
        names.Select(name => new RestaurantMenuOptionRecord(Guid.NewGuid(), name, 0m)).ToArray();
}
