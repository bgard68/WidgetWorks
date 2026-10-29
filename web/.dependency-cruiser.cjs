// Import-graph rules for src/, run by `npm run depcruise` (and Web CI right after the tests).
// Three rules, all errors — a warning in CI is a rule nobody has to follow.
/** @type {import('dependency-cruiser').IConfiguration} */
module.exports = {
  forbidden: [
    {
      name: 'no-circular',
      severity: 'error',
      comment:
        'A cycle means neither module can be understood (or tested) without the other. jsx/tsx ' +
        'modules tolerate them at runtime right up until a re-order turns one into "undefined ' +
        'is not a function", so they are refused while they are still cheap to break.',
      from: {},
      to: { circular: true },
    },
    {
      name: 'not-to-test',
      severity: 'error',
      comment:
        'Production code must not import test files or test helpers: src/test/ (the jsdom ' +
        'setup) and *.test.* exist for vitest only, are excluded from coverage, and nothing ' +
        'guarantees they ship — the bundle only stays free of them if imports never cross that ' +
        'line. Tests importing tests (the setup, a helper) is fine.',
      from: { pathNot: ['[.]test[.](?:ts|tsx)$', '^src/test/'] },
      to: { path: ['[.]test[.](?:ts|tsx)$', '^src/test/'] },
    },
    {
      name: 'not-to-unresolvable',
      severity: 'error',
      comment:
        'An import that does not resolve to a file or an installed package is a typo or a ' +
        'deleted module, and Vite will only say so at build time. Caught here, next to the ' +
        'tests, instead.',
      from: {},
      to: { couldNotResolve: true },
    },
  ],
  options: {
    // Resolve imports into node_modules (so bare specifiers count as resolved) but do not
    // cruise what is inside them — their internals are npm's problem, not this graph's.
    doNotFollow: { path: ['node_modules'] },
    tsConfig: { fileName: 'tsconfig.json' },
    // react-router-dom and friends publish ESM behind `exports` maps; without these fields the
    // resolver reports them unresolvable and rule three fires on every bare import.
    enhancedResolveOptions: {
      exportsFields: ['exports'],
      conditionNames: ['import', 'require', 'node', 'default', 'types'],
      mainFields: ['module', 'main', 'types', 'typings'],
    },
  },
};
