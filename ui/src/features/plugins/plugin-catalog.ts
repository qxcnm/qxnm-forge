export type PluginCategory =
  | "design"
  | "automation"
  | "knowledge"
  | "data"
  | "developer";

export type PluginCatalogId =
  | "product-design"
  | "computer-use"
  | "openai-docs"
  | "data-analytics"
  | "github-workflow";

export type PluginCategoryTranslationKey =
  `marketplace.category.${PluginCategory}`;

export type PluginCatalogTranslationKey =
  | `marketplace.catalog.${PluginCatalogId}.summary`
  | `marketplace.catalog.${PluginCatalogId}.description`
  | `marketplace.catalog.${PluginCatalogId}.tags.${string}`;

export type PluginCatalogTranslator = (
  key: PluginCategoryTranslationKey | PluginCatalogTranslationKey,
) => string;

export interface PluginCatalogEntry {
  readonly pluginId: PluginCatalogId;
  readonly name: string;
  readonly summaryKey: PluginCatalogTranslationKey;
  readonly descriptionKey: PluginCatalogTranslationKey;
  readonly publisher: string;
  readonly version: string;
  readonly category: PluginCategory;
  readonly tagKeys: readonly PluginCatalogTranslationKey[];
  readonly requiredToolIds: readonly string[];
  readonly requiredMethodIds: readonly string[];
  readonly requiredEventTypes: readonly string[];
  readonly capabilityMode: "all" | "any";
  readonly readinessPolicy:
    | "capability_intersection"
    | "experimental_unavailable";
}

export interface PluginCapabilityStatus {
  readonly available: boolean;
  readonly availableToolIds: readonly string[];
  readonly missingToolIds: readonly string[];
  readonly availableMethodIds: readonly string[];
  readonly missingMethodIds: readonly string[];
  readonly availableEventTypes: readonly string[];
  readonly missingEventTypes: readonly string[];
}

/**
 * 插件市场固定分类对应的稳定翻译键。
 *
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export const PLUGIN_CATEGORY_TRANSLATION_KEYS = {
  design: "marketplace.category.design",
  automation: "marketplace.category.automation",
  knowledge: "marketplace.category.knowledge",
  data: "marketplace.category.data",
  developer: "marketplace.category.developer",
} as const satisfies Readonly<Record<PluginCategory, PluginCategoryTranslationKey>>;

/**
 * 当前前端可展示的插件目录元数据，不包含插件实现、私有端点或凭据。
 *
 * 不变量：requiredToolIds 只描述 application service 可以广告的中立工具标识。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export const PLUGIN_CATALOG: readonly PluginCatalogEntry[] = [
  {
    pluginId: "product-design",
    name: "Product Design",
    summaryKey: "marketplace.catalog.product-design.summary",
    descriptionKey: "marketplace.catalog.product-design.description",
    publisher: "QXNM Forge",
    version: "1.0.0",
    category: "design",
    tagKeys: [
      "marketplace.catalog.product-design.tags.interface",
      "marketplace.catalog.product-design.tags.experience",
      "marketplace.catalog.product-design.tags.react",
    ],
    requiredToolIds: ["file.read", "file.write", "file.edit"],
    requiredMethodIds: [],
    requiredEventTypes: [],
    capabilityMode: "all",
    readinessPolicy: "capability_intersection",
  },
  {
    pluginId: "computer-use",
    name: "Computer Use",
    summaryKey: "marketplace.catalog.computer-use.summary",
    descriptionKey: "marketplace.catalog.computer-use.description",
    publisher: "QXNM Forge",
    version: "0.8.0",
    category: "automation",
    tagKeys: [
      "marketplace.catalog.computer-use.tags.desktop",
      "marketplace.catalog.computer-use.tags.automation",
      "marketplace.catalog.computer-use.tags.screen",
    ],
    requiredToolIds: [
      "computer.observe",
      "computer.screenshot",
      "computer.interact",
    ],
    requiredMethodIds: ["approval/respond"],
    requiredEventTypes: ["approval.requested", "approval.resolved"],
    capabilityMode: "all",
    readinessPolicy: "experimental_unavailable",
  },
  {
    pluginId: "openai-docs",
    name: "OpenAI Docs",
    summaryKey: "marketplace.catalog.openai-docs.summary",
    descriptionKey: "marketplace.catalog.openai-docs.description",
    publisher: "QXNM Forge",
    version: "1.1.0",
    category: "knowledge",
    tagKeys: [
      "marketplace.catalog.openai-docs.tags.openai",
      "marketplace.catalog.openai-docs.tags.docs",
      "marketplace.catalog.openai-docs.tags.search",
    ],
    requiredToolIds: ["docs.openai.search"],
    requiredMethodIds: [],
    requiredEventTypes: [],
    capabilityMode: "all",
    readinessPolicy: "capability_intersection",
  },
  {
    pluginId: "data-analytics",
    name: "Data Analytics",
    summaryKey: "marketplace.catalog.data-analytics.summary",
    descriptionKey: "marketplace.catalog.data-analytics.description",
    publisher: "QXNM Forge",
    version: "1.0.0",
    category: "data",
    tagKeys: [
      "marketplace.catalog.data-analytics.tags.analysis",
      "marketplace.catalog.data-analytics.tags.metrics",
      "marketplace.catalog.data-analytics.tags.reporting",
    ],
    requiredToolIds: ["file.read", "process.exec"],
    requiredMethodIds: [],
    requiredEventTypes: [],
    capabilityMode: "all",
    readinessPolicy: "capability_intersection",
  },
  {
    pluginId: "github-workflow",
    name: "GitHub Workflow",
    summaryKey: "marketplace.catalog.github-workflow.summary",
    descriptionKey: "marketplace.catalog.github-workflow.description",
    publisher: "QXNM Forge",
    version: "0.9.0",
    category: "developer",
    tagKeys: [
      "marketplace.catalog.github-workflow.tags.github",
      "marketplace.catalog.github-workflow.tags.git",
      "marketplace.catalog.github-workflow.tags.ci",
    ],
    requiredToolIds: ["file.read", "file.edit", "process.exec"],
    requiredMethodIds: [],
    requiredEventTypes: [],
    capabilityMode: "all",
    readinessPolicy: "capability_intersection",
  },
] as const;

/**
 * 计算插件需求与当前 application service 能力广告的精确交集。
 *
 * 输入：目录项和 initialize 返回的工具、方法及事件 ID；输出：可用、已广告与缺失集合。
 * 不变量：不会合成、补全或改写能力 ID；工具需求为空或 readiness policy 明确冻结时不可用。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export function getPluginCapabilityStatus(
  plugin: PluginCatalogEntry,
  supportedToolIds: readonly string[],
  supportedMethodIds: readonly string[] = [],
  supportedEventTypes: readonly string[] = [],
): PluginCapabilityStatus {
  const supportedTools = new Set(supportedToolIds);
  const supportedMethods = new Set(supportedMethodIds);
  const supportedEvents = new Set(supportedEventTypes);
  const availableToolIds = plugin.requiredToolIds.filter((toolId) =>
    supportedTools.has(toolId),
  );
  const missingToolIds = plugin.requiredToolIds.filter(
    (toolId) => !supportedTools.has(toolId),
  );
  const availableMethodIds = plugin.requiredMethodIds.filter((methodId) =>
    supportedMethods.has(methodId),
  );
  const missingMethodIds = plugin.requiredMethodIds.filter(
    (methodId) => !supportedMethods.has(methodId),
  );
  const availableEventTypes = plugin.requiredEventTypes.filter((eventType) =>
    supportedEvents.has(eventType),
  );
  const missingEventTypes = plugin.requiredEventTypes.filter(
    (eventType) => !supportedEvents.has(eventType),
  );
  const capabilityRequirementsSatisfied =
    plugin.requiredToolIds.length > 0 &&
    missingMethodIds.length === 0 &&
    missingEventTypes.length === 0 &&
    (plugin.capabilityMode === "any"
      ? availableToolIds.length > 0
      : missingToolIds.length === 0);
  const available =
    plugin.readinessPolicy === "capability_intersection" &&
    capabilityRequirementsSatisfied;

  return {
    available,
    availableToolIds,
    missingToolIds,
    availableMethodIds,
    missingMethodIds,
    availableEventTypes,
    missingEventTypes,
  };
}

/**
 * 按页签、分类和用户搜索词过滤固定插件目录。
 *
 * 输入：本地已安装 ID 集合以及非敏感界面筛选条件；输出：保持目录原顺序的条目。
 * 作者：高宏顺
 * 邮箱：18272669457@163.com
 */
export function filterPluginCatalog(
  installedPluginIds: ReadonlySet<string>,
  view: "browse" | "installed",
  category: PluginCategory | "all",
  query: string,
  translate: PluginCatalogTranslator,
): readonly PluginCatalogEntry[] {
  const normalizedQuery = query.trim().toLocaleLowerCase();

  return PLUGIN_CATALOG.filter((plugin) => {
    if (view === "installed" && !installedPluginIds.has(plugin.pluginId)) {
      return false;
    }
    if (category !== "all" && plugin.category !== category) {
      return false;
    }
    if (!normalizedQuery) {
      return true;
    }
    const searchableText = [
      plugin.name,
      translate(plugin.summaryKey),
      plugin.publisher,
      translate(PLUGIN_CATEGORY_TRANSLATION_KEYS[plugin.category]),
      ...plugin.tagKeys.map(translate),
    ]
      .join(" ")
      .toLocaleLowerCase();
    return searchableText.includes(normalizedQuery);
  });
}
