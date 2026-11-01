-- Pandoc filter of scripts/build-docs.sh: turns a page of the Markdown user guide into an HTML page.
-- Links to another page lead to its .html file (README.md files stay as they are), the first heading gives the
-- title of the browser tab, and a page given "contents-href" starts with a link to the contents of its language
-- and, given "other-href", to the same page in the other language.

local function to_html(target)
  if target:match('^%a[%w+.-]*:') then
    return target
  end
  local path, anchor = target:match('^([^#]*)(.*)$')
  if path:match('%.md$') and not path:match('README%.md$') then
    return path:gsub('%.md$', '.html') .. anchor
  end
  return target
end

local function meta_text(meta, name)
  return meta[name] and pandoc.utils.stringify(meta[name]) or nil
end

function Pandoc(doc)
  doc = doc:walk({
    Link = function(link)
      link.target = to_html(link.target)
      return link
    end,
  })

  if not doc.meta.pagetitle then
    for _, block in ipairs(doc.blocks) do
      if block.t == 'Header' and block.level == 1 then
        local title = pandoc.utils.stringify(block)
        if not title:find('RomSieve', 1, true) then
          title = title .. ' · RomSieve'
        end
        doc.meta.pagetitle = title
        break
      end
    end
  end

  local contents_href = meta_text(doc.meta, 'contents-href')
  if contents_href then
    local links = { pandoc.Link(meta_text(doc.meta, 'contents-label'), contents_href) }
    local other_href = meta_text(doc.meta, 'other-href')
    if other_href then
      table.insert(links, pandoc.Space())
      table.insert(links, pandoc.Str('·'))
      table.insert(links, pandoc.Space())
      table.insert(links, pandoc.Link(meta_text(doc.meta, 'other-label'), other_href))
    end
    doc.blocks:insert(1, pandoc.Div({ pandoc.Para(links) }, pandoc.Attr('', { 'guide-nav' })))
  end
  return doc
end
