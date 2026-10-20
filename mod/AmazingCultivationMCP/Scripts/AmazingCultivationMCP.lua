local AcsMcp = GameMain:NewMod("AmazingCultivationMCP")

function AcsMcp:OnBeforeInit()
    print("[AmazingCultivationMCP] OnBeforeInit start")
    local ok, err = xpcall(function()
        xlua.private_accessible(CS.XLua.LuaEnv)
        xlua.private_accessible(CS.XLua.ObjectTranslator)
        local mod = CS.ModsMgr.Instance:FindMod("AmazingCultivationMCP", nil, true)
        if mod == nil then error("active mod data was not found") end
        local dll = CS.System.IO.Path.Combine(mod.Path, "AmazingCultivationMCP.Bridge.dll")
        print("[AmazingCultivationMCP] loading " .. dll)
        local assembly = CS.System.Reflection.Assembly.LoadFrom(dll)
        CS.XiaWorld.LuaMgr.Instance.Env.translator.assemblies:Add(assembly)
        local localAppData = CS.System.Environment.GetEnvironmentVariable("LOCALAPPDATA")
        if localAppData == nil or localAppData == "" then error("LOCALAPPDATA is unavailable") end
        local configDir = CS.System.IO.Path.Combine(localAppData, "AmazingCultivationMCP")
        local config = CS.System.IO.Path.Combine(configDir, "bridge.json")
        print("[AmazingCultivationMCP] config " .. config)
        CS.AmazingCultivationMCP.Bridge.Initialize(config)
    end, function(message)
        return tostring(message) .. "\n" .. debug.traceback()
    end)
    if ok then
        print("[AmazingCultivationMCP] OnBeforeInit complete")
    else
        print("[AmazingCultivationMCP] OnBeforeInit failed: " .. err)
    end
end

function AcsMcp:OnStep(dt)
    self.mcpDiagnosticFrame = (self.mcpDiagnosticFrame or 0) + 1
    if self.mcpDiagnosticFrame == 120 or self.mcpDiagnosticFrame % 900 == 0 then
        print("[AmazingCultivationMCP] " .. CS.AmazingCultivationMCP.Bridge.Diagnostic())
    end
end

function AcsMcp:OnLeave()
    CS.AmazingCultivationMCP.Bridge.Shutdown()
end
