// The household the screenshots are taken of: one pantry, two people, a week
// of cooking. Everything here is invented - no real account, no real database
// - but it is shaped exactly like rows that came off Postgres through
// PowerSync, so the app cannot tell the difference.
//
// Ids are fixed (so a screenshot of /recipe/<id> is reproducible) and look
// like the uuids the server mints. Timestamps are relative to the moment the
// mock starts, so the "3 days ago" notes always read sensibly.

const PANTRY = '7c1f0a42-8f3d-4c2b-9a01-5d6e7f8a9b0c';

const SAM = '3f6c9a10-2b74-4d8e-9f31-0a1b2c3d4e5f';
const DANA = '9a2d4e61-7c58-4b03-8e6f-1d2c3b4a5968';

/// The signed-in user. /api/auth/me hands this back, and every row stamped
/// with SAM shows up as "(Added by Sam Reyes)".
const user = { id: SAM, email: 'sam@maplestreet.test', name: 'Sam Reyes' };

/// Recipes, in the order they were written (the list shows newest first).
/// The Cooklang is the real thing: the steps view, the ingredient names the
/// shopping list matches on, and the editor's highlighting all come out of it.
const recipes = [
  {
    id: 'a1000000-0000-4000-8000-000000000001',
    userId: SAM,
    daysAgo: 21,
    title: 'Focaccia',
    body: `>> time: 18 hours, mostly waiting
>> servings: 12

Whisk @bread flour{500%g}, @fine sea salt{10%g} and @instant yeast{4%g} in a #large bowl{}.

Pour in @lukewarm water{400%ml} and @olive oil{40%ml}, then mix with a #wooden spoon{} until no dry flour is left. The dough will be wetter than you expect; that is the point.

> Cover and leave on the counter for ~{2%hours}, folding the dough over itself every half hour.

Refrigerate overnight, ~{12%hours}.

= Baking day

Oil a #sheet pan{} generously and tip the dough in. Leave it to come back to room temperature, ~{2%hours}.

Dimple the dough all over with oiled fingers, scatter with @flaky salt{} and @rosemary{2%sprigs}, and bake at 230C for ~{22%minutes} until the top is deep gold.`,
  },
  {
    id: 'a1000000-0000-4000-8000-000000000002',
    userId: DANA,
    daysAgo: 14,
    title: 'Red Lentil Dal',
    body: `>> time: 40 minutes
>> servings: 4

Rinse @red lentils{300%g} until the water runs clear.

Warm @ghee{2%tbsp} in a #heavy pot{} and fry @cumin seeds{1%tsp} until they pop, about ~{30%seconds}.

Add @onion{1%large}(finely diced), @garlic{4%cloves} and @ginger{1%thumb}, and cook until soft, ~{8%minutes}.

Stir in @ground turmeric{1%tsp}, @garam masala{2%tsp} and @tomato paste{1%tbsp}.

Add the lentils and @water{1%litre}, bring to a simmer and cook uncovered for ~{25%minutes}, stirring now and then.

Finish with @lemon{1/2} and a handful of @coriander{}.`,
  },
  {
    id: 'a1000000-0000-4000-8000-000000000003',
    userId: SAM,
    daysAgo: 9,
    title: 'Sheet-pan Chicken Thighs',
    body: `>> time: 45 minutes
>> servings: 4

Heat the oven to 220C.

Toss @chicken thighs{6%bone-in} with @olive oil{2%tbsp}, @smoked paprika{2%tsp}, @salt{1%tsp} and @black pepper{}.

Scatter @baby potatoes{700%g}(halved) and @red onion{1} across a #sheet pan{} and sit the thighs on top, skin up.

Roast for ~{40%minutes}, until the skin crackles and the potatoes have taken on colour underneath.

> Spoon the pan juices over everything before it reaches the table.`,
  },
  {
    id: 'a1000000-0000-4000-8000-000000000004',
    userId: DANA,
    daysAgo: 5,
    title: 'Charred Broccoli with Lemon',
    body: `>> time: 20 minutes

Cut @broccoli{1%head} into long spears, stem and all.

Get a #cast iron pan{} very hot, add @olive oil{1%tbsp} and lay the spears down in one layer. Leave them alone for ~{4%minutes}.

Turn, add @garlic{2%cloves}(sliced) and @chilli flakes{}, and cook another ~{3%minutes}.

Off the heat, squeeze over @lemon{1/2} and shower with @parmesan{30%g}.`,
  },
  {
    id: 'a1000000-0000-4000-8000-000000000005',
    userId: SAM,
    daysAgo: 3,
    title: 'Buttermilk Pancakes',
    body: `>> time: 25 minutes
>> servings: 3

Whisk @plain flour{250%g}, @caster sugar{2%tbsp}, @baking powder{2%tsp} and @salt{1/2%tsp} together.

In a #jug{}, beat @buttermilk{350%ml}, @eggs{2} and @melted butter{50%g}.

Fold the wet into the dry - lumps are fine - and rest the batter for ~{10%minutes}.

Cook ladlefuls in a #non-stick pan{} until bubbles hold on the surface, ~{2%minutes} a side.`,
  },
  {
    id: 'a1000000-0000-4000-8000-000000000006',
    userId: DANA,
    daysAgo: 1,
    title: 'Weeknight Beef Chili',
    body: `>> time: 1 hour 15 minutes
>> servings: 6

Brown @beef mince{700%g} hard in a #dutch oven{}, in two batches, and set aside.

Soften @onion{2} and @green pepper{1} in the same pot, then add @garlic{4%cloves}, @chilli powder{2%tbsp}, @ground cumin{1%tbsp} and @oregano{1%tsp}.

Return the beef with @chopped tomatoes{800%g}, @kidney beans{400%g} and @beef stock{300%ml}.

Simmer, half covered, for ~{45%minutes}.

> Better the next day. Make it the night before if you can.`,
  },
];

/// Tags, and which recipes wear them.
const tags = [
  { id: 'b2000000-0000-4000-8000-000000000001', name: 'weeknight', on: [3, 4, 6] },
  { id: 'b2000000-0000-4000-8000-000000000002', name: 'baking', on: [1, 5] },
  { id: 'b2000000-0000-4000-8000-000000000003', name: 'vegetarian', on: [2, 4] },
  { id: 'b2000000-0000-4000-8000-000000000004', name: 'takes all day', on: [1, 6] },
];

/// The current shopping list. Names that match an ingredient in a recipe
/// above pick up a "from Focaccia" note on their own - nothing links them.
const shoppingItems = [
  { name: 'chicken thighs', quantity: '6', unit: '', done: 0, by: SAM },
  { name: 'baby potatoes', quantity: '700', unit: 'g', done: 0, by: SAM },
  { name: 'broccoli', quantity: '1', unit: 'head', done: 0, by: DANA },
  { name: 'bread flour', quantity: '1.5', unit: 'kg', done: 0, by: SAM },
  { name: 'buttermilk', quantity: '350', unit: 'ml', done: 0, by: DANA },
  { name: 'coffee beans', quantity: 'a bag', unit: '', done: 0, by: DANA },
  { name: 'lemon', quantity: '3', unit: '', done: 1, by: SAM },
  { name: 'olive oil', quantity: '1', unit: 'bottle', done: 1, by: SAM },
];

/// The current menu: what the week looks like, with the odd side noted under
/// an entry.
const menuEntries = [
  { recipe: 3, by: SAM, sides: [{ name: 'green salad', done: 0 }] },
  { recipe: 6, by: DANA, sides: [{ name: 'sour cream', done: 1 }, { name: 'cornbread', done: 0 }] },
  { recipe: 2, by: DANA, sides: [] },
  { recipe: 4, by: SAM, sides: [] },
];

const iso = (now, daysAgo, minutes = 0) =>
  new Date(now.getTime() - daysAgo * 86400000 + minutes * 60000).toISOString();

const twoDigit = (n) => String(n).padStart(2, '0');

/// Every row the app will see, in PowerSync's shape: a table, an id and the
/// column values. Built fresh per run so the dates stay relative to today.
export function rows(now = new Date()) {
  const out = [];
  const put = (table, id, data) => out.push({ table, id, data: { id, ...data } });

  put('pantries', PANTRY, {
    user_id: SAM,
    name: 'Maple Street',
    created_at: iso(now, 60),
  });

  put('pantry_members', 'c3000000-0000-4000-8000-000000000001', {
    pantry_id: PANTRY,
    user_id: SAM,
    email: user.email,
    name: user.name,
    status: 'approved',
    created_at: iso(now, 60),
  });
  put('pantry_members', 'c3000000-0000-4000-8000-000000000002', {
    pantry_id: PANTRY,
    user_id: DANA,
    email: 'dana@maplestreet.test',
    name: 'Dana Whitfield',
    status: 'approved',
    created_at: iso(now, 44),
  });

  for (const r of recipes) {
    put('recipes', r.id, {
      pantry_id: PANTRY,
      user_id: r.userId,
      title: r.title,
      body: r.body,
      created_at: iso(now, r.daysAgo),
    });
  }

  tags.forEach((t, ti) => {
    put('tags', t.id, { pantry_id: PANTRY, name: t.name, created_at: iso(now, 30) });
    // One row per (tag, recipe) pair, and the id has to say which pair: two
    // tags on one recipe that shared an id would quietly replace each other.
    for (const n of t.on) {
      put('recipe_tags', `d4000000-0000-4000-8000-${twoDigit(ti)}${twoDigit(n)}00000000`, {
        pantry_id: PANTRY,
        recipe_id: recipes[n - 1].id,
        tag_id: t.id,
        created_at: iso(now, 20),
      });
    }
  });

  const listId = 'e5000000-0000-4000-8000-000000000001';
  put('shopping_lists', listId, {
    pantry_id: PANTRY,
    name: `SL-${twoDigit(now.getMonth() + 1)}${twoDigit(now.getDate())}`,
    created_at: iso(now, 2),
    archived_at: null,
  });
  shoppingItems.forEach((item, i) => {
    put('shopping_items', `f6000000-0000-4000-8000-${twoDigit(i).padStart(12, '0')}`, {
      pantry_id: PANTRY,
      user_id: item.by,
      list_id: listId,
      name: item.name,
      quantity: item.quantity,
      unit: item.unit,
      done: item.done,
      created_at: iso(now, 2, i),
    });
  });

  const menuId = '07000000-0000-4000-8000-000000000001';
  put('menus', menuId, {
    pantry_id: PANTRY,
    name: `M-${twoDigit(now.getMonth() + 1)}${twoDigit(now.getDate())}-zesty-dumpling`,
    created_at: iso(now, 2),
    archived_at: null,
  });
  menuEntries.forEach((entry, i) => {
    const entryId = `18000000-0000-4000-8000-${twoDigit(i).padStart(12, '0')}`;
    put('menu_recipes', entryId, {
      pantry_id: PANTRY,
      user_id: entry.by,
      menu_id: menuId,
      recipe_id: recipes[entry.recipe - 1].id,
      created_at: iso(now, 2, i),
    });
    entry.sides.forEach((side, j) => {
      put('menu_sides', `29000000-0000-4000-8000-${twoDigit(i)}${twoDigit(j).padStart(10, '0')}`, {
        pantry_id: PANTRY,
        menu_recipe_id: entryId,
        name: side.name,
        done: side.done,
        created_at: iso(now, 2, i * 10 + j),
      });
    });
  });

  return out;
}

/// The recipe the detail screenshot opens. Focaccia: long enough to fill a
/// phone, and it uses every bit of Cooklang the editor highlights.
export const featuredRecipeId = recipes[0].id;

export { user };
