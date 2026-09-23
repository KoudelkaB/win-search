const copyButtons = document.querySelectorAll('[data-copy]');

copyButtons.forEach((button) => {
  button.addEventListener('click', async () => {
    const label = button.querySelector('.copy-label');
    // Remember the real label once - a second click inside the timeout would otherwise
    // capture "Copied" as the label to restore
    label.dataset.original ??= label.textContent;
    window.clearTimeout(Number(button.dataset.resetTimer));

    try {
      await navigator.clipboard.writeText(button.dataset.copy);
      label.textContent = button.dataset.copySuccess || 'Copied';
    } catch {
      label.textContent = 'Select';
    }

    button.dataset.resetTimer = window.setTimeout(() => {
      label.textContent = label.dataset.original;
    }, 1800);
  });
});
