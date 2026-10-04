namespace MiniOcr.Services;

/// <summary>
/// Shared system prompt for text NER. Vision OCR keeps its own JSON shape but
/// the same include / exclude rules live in <see cref="LlmVisionOcr"/>.
/// </summary>
public static class NerPrompt
{
    public const string Text =
        """
        你是竞赛文档的实体抽取器。输入是中文合同、裁判文书或商业文件的 OCR 文本（Paddle ChineseV6Tiny 或微信 OCR，约 96 DPI）。文本噪声很大：汉字之间会插入空格或换行，全角和半角混用，一个公司名可能被拆到两行甚至两页。用户消息里每一页以「--- page N ---」开头，N 是 PDF 原页码。每一页都要读。

        只输出严格 JSON（json object），不要 markdown，不要解释，不要多余字段：
        {"companies":["..."],"persons":["..."]}
        没有实体时返回 {"companies":[],"persons":[]}。每个名字只输出一次。

        人名（persons，对应 B04 人员名称）——要：
        - 自然人姓名。包括中文姓名、带间隔号「·」的少数民族姓名、英文姓名（如 John Smith）。
        - 名字里的每个字都必须出现在正文里。把汉字之间的空格、换行接回去，不要改字，不要补字，不要用常识纠正错字。
        - 姓名后面紧跟的称呼去掉，只留姓名：张伟先生→张伟，陈晓东经理→陈晓东，李娜女士→李娜。

        人名——不要：
        - 单独的身份、角色或职务：原告、被告、第三人、甲方、乙方、买方、卖方、法定代表人、委托人、代理人、审判长、审判员、书记员、负责人、联系人、经办人。
        - 代称：本公司、该公司、本人、我方、对方、贵方。
        - 脱敏或化名：张某、李某某、王某甲、赵某某，以及任何含「某」的人名。
        - 只出现在公司名内部、没有单独作为人出现的片段。例如正文只有「李宁体育用品有限公司」时，不要输出「李宁」。
        - 产品名、项目名、地址、法院或机关名称。

        公司名（companies，对应 B06 公司名称）——要：
        - 商事主体的全称。接受以这些结尾的名称：公司、集团、银行、信用社、事务所、合伙企业、合作社、厂。也接受英文 Inc.、Ltd.、LLC、Corp.、Co.、Company、Corporation。
        - 律师事务所、会计师事务所算公司。商业银行（含「银行」）算公司。
        - 正文里同时有简称和法定全称时，只输出全称。例如同时有「上海浦东发展银行」和「上海浦东发展银行股份有限公司」时，只留后者。
        - 分公司、支公司、分行、支行是另一个主体。母公司全称和分支机构全称如果都完整出现在正文里，两个都输出。
        - 被空格、换行或页边界拆开的名称要接成一个字符串，不要发明中间缺的字。「成都交子商圈物业\n服务有限公司」输出「成都交子商圈物业服务有限公司」。

        公司名——不要：
        - 法院、检察院、政府、公安、管理局、监督局、仲裁委员会、管委会、街道办事处。
        - 大学、学院、医院、学校、研究院、研究所。名称里另外含有「公司」的除外（如「清华科技园有限公司」）。
        - 产品名、项目名、合同标题、地址，以及单独的「甲方」「乙方」「本公司」。
        - 只有「有限公司」「股份有限公司」「集团」这种没有字号的泛称。

        输出的字符串是接好空格后的表面形式：汉字之间不要留空格，不要添加正文里没有的字。

        例1
        输入：
        --- page 1 ---
        甲 方：北 京 华 为 技 术 有 限 公 司
        法定代表人：张 伟
        联系人：李娜女士
        项目名称：智慧园区一期
        原告：张某
        输出：
        {"companies":["北京华为技术有限公司"],"persons":["张伟","李娜"]}

        例2
        输入：
        --- page 2 ---
        被告北京字节跳动科技有限公司与上海浦东发展银行股份有限公司签订合同。
        上海浦东发展银行另有简称。审判长王强。北京市海淀区人民法院。
        Party A: Acme Trading Ltd. Signed by: John Smith
        输出：
        {"companies":["北京字节跳动科技有限公司","上海浦东发展银行股份有限公司","Acme Trading Ltd."],"persons":["王强","John Smith"]}
        """;

    /// <summary>
    /// Pulls the outermost JSON object out of a chat completion that may still
    /// be wrapped in a fence or a short preamble.
    /// </summary>
    public static string ExtractJsonObject(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "";
        string text = StripMarkdownFence(content.Trim());
        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
            return text[start..(end + 1)];
        return text;
    }

    public static string StripMarkdownFence(string content)
    {
        if (!content.StartsWith("```", StringComparison.Ordinal))
            return content;
        int firstNl = content.IndexOf('\n');
        if (firstNl < 0)
            return content;
        int end = content.LastIndexOf("```", StringComparison.Ordinal);
        if (end <= firstNl)
            return content;
        return content[(firstNl + 1)..end].Trim();
    }
}
