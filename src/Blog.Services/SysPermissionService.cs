﻿using Blog.Common.Cache;
using Blog.Common.Utils;
using Blog.Entities;
using Blog.Entities.Dtos;
using Blog.IRepository;
using Blog.IServices;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Blog.Services
{
    public class SysPermissionService : BaseService<SysPermission>, ISysPermissionService
    {
        private ISysPermissionRepository _sysPermissionRepository;
        private ISysModuleService _sysModuleService;
        private ISysRoleService _sysRoleService;
        private ISysButtonService _sysButtonService;

        public SysPermissionService(ISysPermissionRepository sysPermissionRepository, ISysModuleService sysModuleService, ISysRoleService sysRoleService, ISysButtonService sysButtonService) : base(sysPermissionRepository)
        {
            _sysPermissionRepository = sysPermissionRepository;
            _sysModuleService = sysModuleService;
            _sysRoleService = sysRoleService;
            _sysButtonService = sysButtonService;
        }

        /// <summary>
        /// 分配权限
        /// </summary>
        /// <param name="roleId">角色id</param>
        /// <param name="permission">分配权限菜单按钮id集合</param>
        /// <param name="createUserId">创建人userid</param>
        /// <returns></returns>
        public async Task<OperateResult> Save(string roleId, List<string> permission, string createUserId)
        {
            OperateResult result = new OperateResult();
            if (!permission.Any())
            {
                result.Message = "分配的权限不能为空";
                return result;
            }
            int count = await _sysRoleService.QueryableCountAsync(c => c.RoleId == roleId);
            if (string.IsNullOrWhiteSpace(roleId) || count == 0)
            {
                result.Message = "所分配角色不存在";
                return result;
            }

            var menus = await _sysModuleService.QueryableAsync(m => permission.Contains(m.ModuleId));
            var buttons = await _sysButtonService.QueryableAsync(b => permission.Contains(b.ButtonId));

            if (!menus.Any() && !buttons.Any())
            {
                result.Message = "分配的权限不能为空";
                return result;
            }

            //删除现有权限重新分配权限
            await DeleteRemoveCacheAsync(c => c.AuthorizeId == roleId);

            List<SysPermission> list = new List<SysPermission>();
            if (menus.Any())
            {
                list = (from m in menus select new SysPermission() { PermissionId = SnowflakeUtil.NextStringId(), AuthorizeId = roleId, ModuleType = 1, ObjectType = 1, SysModuleId = m.ModuleId }).ToList();
            }
            if (buttons.Any())
            {
                var bs = (from b in buttons select new SysPermission() { PermissionId = SnowflakeUtil.NextStringId(), AuthorizeId = roleId, ModuleType = 2, ObjectType = 1, SysModuleId = b.ButtonId }).ToList();
                list.AddRange(bs);
            }
            //删除缓存
            Redis.DelPattern(roleId);
            return await InsertRemoveCacheAsync(list);
        }

        /// <summary>
        /// 获取指定角色、部门、用户所有可访问的菜单按钮信息
        /// </summary>
        /// <param name="authorrizeId">角色ID、部门ID、用户ID</param>
        /// <returns></returns>
        public async Task<object> GetMenuAndButton(params string[] authorrizeId)
        {
            string key = string.Join('_', authorrizeId.OrderBy(o => o).ToArray());

            //获取缓存
            object data = Redis.Current.Get<object>(key);
            if (data != null)
            {
                return data;
            }
            var author = await QueryableAsync(p => authorrizeId.Contains(p.AuthorizeId));
            var mids = author.Select(c => c.SysModuleId);
            //所有菜单
            var menuList = await _sysModuleService.QueryableAsync(m => mids.Contains(m.ModuleId) && m.EnabledMark == true && m.DeleteMark == false);
            //所有按钮
            var buttonList = await _sysButtonService.QueryableAsync(b => mids.Contains(b.ButtonId));
            List<MenuSettingDto> topMenu = new List<MenuSettingDto>();
            Dictionary<string, List<MenuSettingDto>> childMenu = new Dictionary<string, List<MenuSettingDto>>();
            Dictionary<string, List<SysButton>> tool = new Dictionary<string, List<SysButton>>();
            Dictionary<string, List<SysButton>> row = new Dictionary<string, List<SysButton>>();
            foreach (var menu in menuList.Where(m => m.ParentId == "0").OrderBy(o => o.SortCode))
            {
                var child = GetChildMenuAndButton(menuList, buttonList, menu.ModuleId, tool, row);
                topMenu.Add(new MenuSettingDto()
                {
                    id = menu.EnCode,
                    title = menu.FullName,
                    icon = menu.Icon,
                    href = menu.UrlAddress
                });
                childMenu[menu.EnCode] = child;
            }
            data = new { topMenus = topMenu, childMenus = childMenu, rowButtons = row, toolButtons = tool };
            //设置缓存
            Redis.Current.Set(key, data);
            return data;
        }

        /// <summary>
        /// 检验权限
        /// </summary>
        /// <param name="authorizeId">授权id（角色ID/部门ID/用户ID）</param>
        /// <param name="url">授权地址</param>
        /// <returns></returns>
        public bool CheckPermission(string authorizeId, string url)
        {
            url = url.ToLower();
            List<SysModule> menus;
            List<SysButton> buttons;
            var permissions = QueryableCache(p => p.AuthorizeId == authorizeId).Select(c => c.SysModuleId).ToList();
            if (permissions.Any())
            {
                menus = _sysModuleService.QueryableCache(m => permissions.Contains(m.ModuleId));
                buttons = _sysButtonService.QueryableCache(b => permissions.Contains(b.ButtonId));
                if (menus.Where(m => m.UrlAddress != null && m.UrlAddress.ToLower() == url).Any() || buttons.Where(b => b.UrlAddress != null && b.UrlAddress.ToLower() == url).Any())
                {
                    return true;
                }
            }

            return false;
        }


        /// <summary>
        /// 获取菜单和菜单下的按钮
        /// </summary>
        /// <param name="modules">所有菜单</param>
        /// <param name="buttons">所有按钮</param>
        /// <param name="parentId">上级菜单id</param>
        /// <param name="toolButton">工具栏按钮</param>
        /// <param name="rowButton">行内按钮</param>
        /// <returns></returns>
        private List<MenuSettingDto> GetChildMenuAndButton(List<SysModule> modules, List<SysButton> buttons, string parentId, Dictionary<string, List<SysButton>> toolButton, Dictionary<string, List<SysButton>> rowButton)
        {
            List<MenuSettingDto> list = new List<MenuSettingDto>();
            foreach (var item in modules.Where(m => m.ParentId == parentId).OrderBy(o => o.SortCode))
            {
                MenuSettingDto ms = new MenuSettingDto();
                ms.id = item.EnCode;
                ms.title = item.FullName;
                ms.icon = item.Icon;
                ms.href = item.UrlAddress;
                ms.children = GetChildMenuAndButton(modules, buttons, item.ModuleId, toolButton, rowButton);
                list.Add(ms);
                var temptool = buttons.Where(b => b.SysModuleId == item.ModuleId && b.Location == 1).OrderBy(o => o.SortCode);
                if (temptool.Any())
                {
                    toolButton.Add(item.EnCode, temptool.ToList());
                }
                var temprow = buttons.Where(b => b.SysModuleId == item.ModuleId && b.Location == 2).OrderBy(o => o.SortCode);
                if (temprow.Any())
                {
                    rowButton.Add(item.EnCode, temprow.ToList());
                }
            }
            return list;
        }

    }
}
