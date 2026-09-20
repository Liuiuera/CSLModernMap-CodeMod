import { ModRegistrar } from "cs2/modding";

// 当前入口使用游戏原生的“选项 → 模组”设置 API，不再注入暂停界面。
// 保留官方 UI 工程骨架，供后续确实需要自定义界面时使用。
const register: ModRegistrar = () => {};

export default register;
