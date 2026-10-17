using FftTrainer.Core;

namespace FftTrainer.Core.Tests;

// 测试用的游戏文本：原先写死在 GameData 里的简中名称（现在运行时从游戏封包读），断言里还在用这些名字
internal static class TestText
{
    public static readonly string[] Jobs =
    [
        "见习战士", "道具士", "骑士", "弓箭手", "武僧", "白魔道士", "黑魔道士",
        "时魔道士", "召唤士", "盗贼", "话术士", "祈祷士", "风水士", "龙骑士",
        "武士", "忍者", "算术士", "吟游诗人", "舞者", "模仿士",
    ];

    public static GameTextCatalog Legacy()
    {
        var jobs = new Dictionary<int, string> { [0x01] = Jobs[0] };
        for (var i = 0; i < Jobs.Length; i++)
            jobs[0x4A + i] = Jobs[i];
        var catalog = new GameTextCatalog();
        catalog.Load(new GameTextData(
            Units: new() { [1] = "拉姆萨", [329] = "齐克", [468] = "麦克尼尔", [459] = "霍特曼", [625] = "香奈尔", [695] = "纽特", [601] = "桑德拉" },
            Jobs: jobs,
            Commands: [],
            Items: new()
            {
                [0xF0] = "药水", [0xF1] = "高级药水", [0xF2] = "超级药水", [0xF3] = "以太",
                [0xF4] = "高级以太", [0xF5] = "万能药", [0xF6] = "解毒剂", [0xF7] = "眼药",
                [0xF8] = "回音草", [0xF9] = "少女之吻", [0xFA] = "软化剂", [0xFB] = "圣水",
                [0xFC] = "万灵药", [0xFD] = "凤凰之尾", [0x9D] = "皮帽", [0xBA] = "布衣",
                [0x13] = "宽刃剑", [0x101] = "学院剑", [0x102] = "学院扁帽",
                [0x103] = "学院外套", [0x104] = "职业戒指",
            },
            ItemCategories: [],
            Abilities: []));
        return catalog;
    }
}
