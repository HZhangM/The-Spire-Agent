-- ============================================================
-- STS2 Auto-Play: Default Strategy Script
-- ============================================================
-- This script is called once per decision point during combat.
-- It receives the current battle state and must return a single action:
--   play_card(card_index, target_index)   - play a card
--   play_card_no_target(card_index)       - play a card without target
--   use_potion(potion_index, target_index) - use a potion
--   end_turn()                            - end the turn
--
-- The engine calls decide_action() repeatedly until it returns end_turn().
-- Each call receives a FRESH state snapshot (energy/hand updated after each play).
--
-- Available helper functions:
--   find_card(state, {type="Attack", name="Strike", max_cost=2})
--   find_cards(state, {type="Skill"})
--   lowest_hp_enemy(state) -> enemy table or nil
--   total_incoming_damage(state) -> number
--   has_power(powers_table, "PowerName") -> bool
--   get_power_amount(powers_table, "PowerName") -> number
--   log("message") -> prints to game log
-- ============================================================

-- Track state across calls within the same turn
local actions_this_turn = 0

function decide_action(state)
    actions_this_turn = actions_this_turn + 1

    -- Minimal debug: test if log works
    log("=== decide_action called, turn action #" .. tostring(actions_this_turn) .. " ===")

    local p = state.player
    if p == nil then
        log("ERROR: state.player is nil!")
        return end_turn()
    end

    local energy = p.energy
    log("energy=" .. tostring(energy))

    local hand = state.hand
    if hand == nil then
        log("ERROR: state.hand is nil!")
        return end_turn()
    end

    log("hand type=" .. tostring(type(hand)))

    -- Count hand cards using numeric index (MoonSharp C# tables are 1-indexed arrays)
    local hand_count = 0
    local playable_count = 0
    local i = 1
    while hand[i] ~= nil do
        hand_count = hand_count + 1
        if hand[i].can_play then
            playable_count = playable_count + 1
        end
        i = i + 1
    end
    log("hand_size=" .. tostring(hand_count) .. " playable=" .. tostring(playable_count))

    -- No energy left -> end turn
    if energy <= 0 then
        actions_this_turn = 0
        return end_turn()
    end

    if playable_count == 0 then
        actions_this_turn = 0
        return end_turn()
    end

    -- ========================================
    -- PHASE 1: Emergency potion usage
    -- ========================================
    if actions_this_turn == 1 then
        local incoming = total_incoming_damage(state)
        -- Use healing potion if we might die
        if incoming > p.hp + p.block then
            for _, potion in ipairs(state.potions) do
                local name = string.lower(potion.name)
                if name:find("heal") or name:find("fairy") or name:find("fruit") then
                    log("Emergency heal potion: " .. potion.name)
                    return use_potion(potion.index, nil)
                end
            end
        end
    end

    -- ========================================
    -- PHASE 2: Check lethal threat
    -- ========================================
    local incoming = total_incoming_damage(state)
    local need_block = incoming - p.block
    local is_lethal = incoming > p.hp + p.block

    -- ========================================
    -- PHASE 3: Play Power cards (if safe)
    -- ========================================
    if not is_lethal then
        local power_card = find_card(state, {type = "Power"})
        if power_card then
            log("Playing power: " .. power_card.name)
            return play_card_no_target(power_card.index)
        end
    end

    -- ========================================
    -- PHASE 4: Block if facing lethal
    -- ========================================
    if is_lethal and need_block > 0 then
        local block_card = find_card(state, {type = "Skill"})
        if block_card then
            log("Blocking vs lethal: " .. block_card.name)
            if block_card.target_type == "AnyEnemy" then
                local target = lowest_hp_enemy(state)
                if target then
                    return play_card(block_card.index, target.index)
                end
            end
            return play_card_no_target(block_card.index)
        end
    end

    -- ========================================
    -- PHASE 5: Play attack cards
    -- ========================================
    local attack = find_card(state, {type = "Attack"})
    if attack then
        local target = lowest_hp_enemy(state)
        if target and attack.target_type == "AnyEnemy" then
            log("Attacking " .. target.name .. " with " .. attack.name)
            return play_card(attack.index, target.index)
        else
            return play_card_no_target(attack.index)
        end
    end

    -- ========================================
    -- PHASE 6: Play remaining skills
    -- ========================================
    local skill = find_card(state, {type = "Skill"})
    if skill then
        log("Playing skill: " .. skill.name)
        if skill.target_type == "AnyEnemy" then
            local target = lowest_hp_enemy(state)
            if target then
                return play_card(skill.index, target.index)
            end
        end
        return play_card_no_target(skill.index)
    end

    -- ========================================
    -- PHASE 7: Play anything remaining
    -- ========================================
    local any = find_card(state, {})
    if any then
        if any.target_type == "AnyEnemy" then
            local target = lowest_hp_enemy(state)
            if target then
                return play_card(any.index, target.index)
            end
        end
        return play_card_no_target(any.index)
    end

    -- Nothing left to do
    actions_this_turn = 0
    return end_turn()
end

-- Optional: called after each combat ends
function on_combat_end(state, victory, remaining_hp)
    if victory then
        log("Combat won! HP remaining: " .. remaining_hp)
    else
        log("Combat lost!")
    end
    actions_this_turn = 0
end
