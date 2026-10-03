import fs from 'node:fs';
import path from 'node:path';
import ts from 'typescript';

const arg = (name) => {
  const i = process.argv.indexOf(name);
  return i === -1 || i + 1 >= process.argv.length ? null : process.argv[i + 1];
};

const root = arg('--root') ? path.resolve(arg('--root')) : process.cwd();
const out = arg('--out');

if (!out) {
  console.error('missing --out');
  process.exit(2);
}

const EXTENSIONS = new Set(['.ts', '.tsx', '.jsx']);
const SKIPPED = new Set([
  'node_modules', '.git', '.vs', '.idea', 'bin', 'obj', 'dist', 'build', 'out', 'coverage', '.next'
]);

function walk(dir, acc = []) {
  let entries = [];
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch {
    return acc;
  }
  for (const entry of entries) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      if (!SKIPPED.has(entry.name)) walk(full, acc);
    } else if (entry.isFile() && EXTENSIONS.has(path.extname(entry.name).toLowerCase())) {
      acc.push(full);
    }
  }
  return acc;
}

const scriptKind = (file) => {
  const ext = path.extname(file).toLowerCase();
  if (ext === '.tsx') return ts.ScriptKind.TSX;
  if (ext === '.jsx') return ts.ScriptKind.JSX;
  if (ext === '.js' || ext === '.mjs' || ext === '.cjs') return ts.ScriptKind.JS;
  return ts.ScriptKind.TS;
};

const language = (file) => {
  const ext = path.extname(file).toLowerCase();
  if (ext === '.tsx') return 'tsx';
  if (ext === '.jsx') return 'jsx';
  return 'typescript';
};

const rel = (file) => path.relative(root, file).replaceAll('\\', '/');

const snippet = (lines, line, afterLines = 2, maxChars = 500) => {
  const start = Math.max(0, line - 1);
  const end = Math.min(lines.length, line + afterLines + 1);
  const text = lines.slice(start, end).join('\n').trim();
  return text.length > maxChars ? text.slice(0, maxChars) + '…' : text;
};

const isFunctionLike = (node) => ts.isArrowFunction(node) || ts.isFunctionExpression(node);

const hasJsx = (node) => {
  let found = false;
  const visit = (n) => {
    if (found || !n) return;
    if (ts.isJsxElement(n) || ts.isJsxSelfClosingElement(n) || ts.isJsxFragment(n)) {
      found = true;
      return;
    }
    ts.forEachChild(n, visit);
  };
  visit(node);
  return found;
};

const files = walk(root);
const options = {
  noLib: true,
  allowJs: true,
  jsx: ts.JsxEmit.Preserve,
  skipLibCheck: true,
  module: ts.ModuleKind.ESNext,
  moduleResolution: ts.ModuleResolutionKind.NodeNext,
  target: ts.ScriptTarget.Latest,
  noEmit: true,
  types: []
};

const program = ts.createProgram(files, options, ts.createCompilerHost(options));
const checker = program.getTypeChecker();

const symbols = [];
const references = [];
const nameSet = new Set();
const declPos = new Set();
const seenSymbols = new Set();
const symbolToId = new Map();

const resolveSymbol = (node) => {
  let symbol = checker.getSymbolAtLocation(node);
  if (symbol && (symbol.flags & ts.SymbolFlags.Alias)) {
    symbol = checker.getAliasedSymbol(symbol);
  }
  return symbol;
};

const fallbackId = (file, line, column) => `${language(file)}:${file}:${line}:${column}`;

const symbolId = (symbol, file, line, column) => {
  if (symbol) {
    try {
      const id = checker.getFullyQualifiedName(symbol);
      if (id) return id;
    } catch {
      // Fall through to the location-based id.
    }
  }
  return fallbackId(file, line, column);
};

const signatureFor = (node, nameNode) => {
  try {
    if (ts.isFunctionDeclaration(node)
      || ts.isMethodDeclaration(node)
      || ts.isMethodSignature(node)
      || ts.isConstructorDeclaration(node)
      || ts.isFunctionExpression(node)
      || ts.isArrowFunction(node)
      || ts.isCallSignatureDeclaration(node)
      || ts.isConstructSignatureDeclaration(node)) {
      const signature = checker.getSignatureFromDeclaration(node);
      return signature ? checker.signatureToString(signature) : '';
    }

    if (ts.isVariableDeclaration(node)
      || ts.isPropertyDeclaration(node)
      || ts.isPropertySignature(node)
      || ts.isGetAccessorDeclaration(node)
      || ts.isSetAccessorDeclaration(node)
      || ts.isTypeAliasDeclaration(node)) {
      const type = checker.getTypeAtLocation(nameNode || node);
      return type ? checker.typeToString(type) : '';
    }
  } catch {
    // Signatures are best-effort; return empty when the checker cannot describe the node.
  }
  return '';
};

for (const file of files) {
  const text = fs.readFileSync(file, 'utf8');
  const sf = program.getSourceFile(file) || ts.createSourceFile(file, text, ts.ScriptTarget.Latest, true, scriptKind(file));
  const lines = text.split(/\r?\n/);
  const fileRel = rel(file);

  const add = (node, nameNode, kind) => {
    if (!nameNode || !ts.isIdentifier(nameNode)) return;
    const name = nameNode.text;
    if (!name) return;

    const pos = nameNode.getStart(sf);
    const { line, character } = sf.getLineAndCharacterOfPosition(pos);
    declPos.add(`${fileRel}:${pos}`);
    nameSet.add(name);

    const symbol = resolveSymbol(nameNode);
    if (symbol) {
      if (seenSymbols.has(symbol)) return;
      seenSymbols.add(symbol);
    }

    const id = symbolId(symbol, fileRel, line + 1, character + 1);
    if (symbol) symbolToId.set(symbol, id);

    symbols.push({
      id,
      name,
      kind,
      language: language(file),
      qualifiedName: id,
      file: fileRel,
      line: line + 1,
      column: character + 1,
      signature: signatureFor(node, nameNode),
      snippet: snippet(lines, line, 5)
    });
  };

  const visit = (node) => {
    if (ts.isClassDeclaration(node) && node.name) add(node, node.name, 'class');
    else if (ts.isInterfaceDeclaration(node) && node.name) add(node, node.name, 'interface');
    else if (ts.isEnumDeclaration(node) && node.name) add(node, node.name, 'enum');
    else if (ts.isEnumMember(node) && node.name) add(node, node.name, 'enumMember');
    else if (ts.isTypeAliasDeclaration(node) && node.name) add(node, node.name, 'type');
    else if (ts.isModuleDeclaration(node) && node.name && ts.isIdentifier(node.name)) add(node, node.name, 'module');
    else if (ts.isFunctionDeclaration(node) && node.name) add(node, node.name, 'function');
    else if (ts.isMethodDeclaration(node) && node.name) add(node, node.name, 'method');
    else if (ts.isPropertyDeclaration(node) && node.name) add(node, node.name, 'property');
    else if (ts.isGetAccessor(node) && node.name) add(node, node.name, 'getter');
    else if (ts.isSetAccessor(node) && node.name) add(node, node.name, 'setter');
    else if (ts.isVariableStatement(node)) {
      for (const decl of node.declarationList.declarations) {
        if (!ts.isIdentifier(decl.name)) continue;
        const initializer = decl.initializer;
        const kind = initializer && hasJsx(initializer) ? 'component'
          : initializer && isFunctionLike(initializer) ? 'function'
          : 'variable';
        add(decl, decl.name, kind);
      }
    }

    ts.forEachChild(node, visit);
  };

  visit(sf);
}

for (const file of files) {
  const text = fs.readFileSync(file, 'utf8');
  const sf = program.getSourceFile(file) || ts.createSourceFile(file, text, ts.ScriptTarget.Latest, true, scriptKind(file));
  const fileRel = rel(file);

  const visit = (node) => {
    if (ts.isIdentifier(node)) {
      const name = node.text;
      if (nameSet.has(name)) {
        const pos = node.getStart(sf);
        if (!declPos.has(`${fileRel}:${pos}`)) {
          const symbol = resolveSymbol(node);
          const target = symbol ? symbolToId.get(symbol) : undefined;
          if (target) {
            const { line, character } = sf.getLineAndCharacterOfPosition(pos);
            references.push({
              target,
              name,
              file: fileRel,
              line: line + 1,
              column: character + 1,
              snippet: ''
            });
          }
        }
      }
    }
    ts.forEachChild(node, visit);
  };

  visit(sf);
}

fs.writeFileSync(out, JSON.stringify({ symbols, references }));
