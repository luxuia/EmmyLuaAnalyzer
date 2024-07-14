---@class Base
local base = {desc = 'CLASSB'} 

---类型A
---@class A : Base
local classA = {desc = 'CLASSA'}


---@param class A
---@return A
function test(class)
end

---@type A
local a 

---@type A[]
local a_list


---@type table<string, A>
local a_tabla

---@alias enuma interger | 1 | 2 | 3
---@param a enuma
function test_enum(a)
end

test_enum(1)


local t = {}
---@mapping new
t:ctor(a, b)

t:new()

---@enum IO: number
IO = {
    Input = 1,
    Output = '2',
}
