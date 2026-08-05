const copyButtons = document.querySelectorAll('[data-copy]');

copyButtons.forEach((button) => {
  button.addEventListener('click', async () => {
    const label = button.querySelector('.copy-label');
    const originalLabel = label.textContent;

    try {
      await navigator.clipboard.writeText(button.dataset.copy);
      label.textContent = 'Copied';
    } catch {
      label.textContent = 'Select';
    }

    window.setTimeout(() => {
      label.textContent = originalLabel;
    }, 1800);
  });
});
